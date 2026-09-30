# Multi-Backend Desktop Hub — Design Spec

- Date: 2026-09-22
- Status: approved (C bertahap)
- Scope: `src/desktop/Eling.Desktop` (hub) + `src/backend/Eling.Backend` (auth opt-in) + tests
- Phasing: Fase 1 = multi-backend + auth + 2 mode window (free switch + per-window pin) + status bar + auto-reconnect. Fase 2 = tabs dalam 1 window.

## 1. Context & Goals

Desktop Eling hari ini single-backend: `BackendSupervisor` probe `127.0.0.1:4417/4317` lalu spawn `eling-backend.exe` / `dotnet run`. User minta hub yang bisa kelola banyak backend (local + remote via IP/DNS), pindah-pindah instan, multi-window beda backend, tab, latency di status bar, auto-reconnect kalau backend goyang, auth Bearer, dan remote bisa IP/DNS (LAN, VPS, tunnel).

Goals:
- Desktop jadi hub: tambah/edit/hapus koneksi `{ name, baseUrl, token? }`, switch <1s, multi-window & tabs hidup bareng.
- Remote bebas `http://host:port` / `https://dns/path`, validasi URL, support IP literal & DNS.
- Auth opt-in: local tanpa token tetap jalan; remote bisa pakai `Authorization: Bearer <token>` kalau backend set `ELING_API_TOKEN`.
- Status bar per window tampil `name • 42ms 🟢 | online/reconnecting/🔒`.
- Auto-reconnect tahan banting untuk health poll + SSE + GET retry.
- Sidecar local: desktop auto-launch `eling-backend.exe` side-by-side kalau belum hidup.

Non-goals (YAGNI):
- No DPAPI/Keychain dulu — token plain di `desktop-settings.json` (iterasi berikut).
- No backend registry baru / discovery; remote = URL+token manual.
- No DB baru untuk connection registry.
- No mTLS / OAuth — Bearer cukup.

## 2. Architecture

### Backend (opt-in, no breaking local)

```
Kestrel pipeline: ApiAuthMiddleware -> DashboardRoutes -> Endpoints
                    |                      |
                    +-> skip /health       +-> /health returns { authRequired }
                    +-> if ELING_API_TOKEN empty: pass
                    +-> else check Authorization: Bearer <token> for /api/* -> 401 if mismatch
```

- Single-binary ownership tetap: port probe wins, peers skip Kestrel (existing `DashboardPort`/`HttpLoop`).
- No change ke `Eling.Core` memory domain.

### Desktop Hub

```
DesktopSettingsStore (JSON)  ->  ConnectionRegistry (in-memory + probe)
        |                               |
        +-> SettingsDocument            +-> status: reachable/authRequired/unreachable
            { Connections[],               latencyMs, lastProbeAt
              ActiveConnectionId }        ConnectionsChanged event
        |
WindowFactory -> MainWindow + MainViewModel(connectionId)
        |           |-> header: dropdown + indicator
        |           |-> StatusBar (Dock.Bottom): name • ms • state
        |           |-> ElingApiClient (per-window scoped via connectionId)
        |           |-> SseClient (per-window, token-aware)
        |           +-> LatencyProbe + HealthPoller (per active connection)
```

- `ConnectionRegistry` singleton; tiap window punya `ActiveConnectionId` di ViewModel.
- `ElingApiClient` factory `CreateFor(connectionId)`: `GetClient()` inject header kalau token ada. Reuse semua API method.
- `isLocal` flag: hanya entry local yang lewat `BackendSupervisor` spawn; remote tidak.

## 3. Components (files)

### Backend — edit + baru

- `src/backend/Eling.Backend/Bootstrap/ApiAuthMiddleware.cs` (baru): baca `ELING_API_TOKEN` (env, trim), skip `/health`, validasi `Authorization: Bearer ...` untuk `/api/*`, 401 JSON `{ error:"unauthorized" }` jika mismatch.
- `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` (edit): register middleware sebelum `Map*`.
- `src/backend/Eling.Backend/Endpoints/HealthEndpoint.cs` (edit): tambah `authRequired: bool` di response.

### Desktop — baru

- `src/desktop/Eling.Desktop/Models/BackendConnection.cs`: `record BackendConnection(string Id /* ulid */, string Name, string BaseUrl, string? Token, bool IsLocal)`.
- `src/desktop/Eling.Desktop/Services/ConnectionRegistry.cs`: load/save via `DesktopSettingsStore`, validasi `ValidateUrl` (hanya `http://`/`https://`, tolak `file://`), probe `/health` async (3s timeout, Stopwatch), state `ConnectionHealth { Status, LatencyMs, LastProbeAt, AuthRequired }`, event `ConnectionsChanged`.
- `src/desktop/Eling.Desktop/Services/WindowFactory.cs`: `Create(string? initialConnectionId)` -> `MainWindow` + `MainViewModel`.
- `src/desktop/Eling.Desktop/Services/LatencyProbe.cs` (or inside Registry): poll `/health` per active connection, 5s interval + backoff.
- `src/desktop/Eling.Desktop/Views/ConnectionManagerView.axaml` + `.cs` + `ViewModels/ConnectionManagerViewModel.cs`: CRUD koneksi (name/url/token, Test button -> `GET /health` + `GET /api/memories?limit=1`).
- `src/desktop/Eling.Desktop/Views/StatusBarView.axaml` (or inline in MainWindow): bottom bar.

### Desktop — edit tipis

- `Services/DesktopSettingsStore.cs`: extend `SettingsDocument` -> `record SettingsDocument(string? BackendUrl, WindowBounds? Window, List<BackendConnection>? Connections, string? ActiveConnectionId)`. Migrasi: jika `Connections` null/empty -> inject `[ { local, http://127.0.0.1:4417, isLocal:true } ]`. Keep `BackendUrl` for compat, sync ke Connections[0] jika ada.
- `Services/ElingApiClient.cs`: ctor terima `Func<BackendConnection>`, atau `Func<string> baseUrl + Func<string?> token`. `GetClient()` set `Authorization` header jika token non-empty. No method duplication.
- `Services/SseClient.cs`: terima `baseUrl+token` per connection, header Bearer jika ada, hook `Last-Event-Id` resume.
- `Services/BackendSupervisor.cs`: hanya untuk `IsLocal`; remote skip spawn. Window kedua local reuse `ActivePort`.
- `ViewModels/MainViewModel.cs` + `Views/MainWindow.axaml`: header dropdown (ItemsSource = Registry.Connections), indicator 🟢/🟡/🔴/🔒, Manage button, `Ctrl+N` new window command. Fase 2: replace content dengan `TabControl` (tiap `TabItem` = connection, DataTemplate reuse same ViewModel type dengan connectionId berbeda).
- `App.axaml.cs`: register `ConnectionRegistry` singleton, `WindowFactory`, wiring DI.

## 4. Data Flow

### Boot

1. `App.OnFrameworkInitializationCompleted` -> `DesktopSettingsStore.ReadDocument()` -> `ConnectionRegistry.Load()`.
2. Jika no `local` entry -> inject default local.
3. Jika active connection `IsLocal` -> `BackendSupervisor.EnsureBackendRunningAsync()` (probe 4417/4317/spawn side-by-side).
4. `MainWindow` pertama pakai `ActiveConnectionId` (fallback local).
5. Subscribe `Registry.ConnectionsChanged` -> dropdown live.
6. Start `HealthPoller` + `LatencyProbe` untuk active connection; start `SseClient.Connect(active)`.

### Switch (dropdown / tab / new window)

1. User pick connection -> VM `ActiveConnectionId = id` -> `Registry.SetActive(id)` persist JSON.
2. `ElingApiClient` next call pakai baseUrl+token baru.
3. `SseClient.Disconnect()` + `Connect(new baseUrl, token)` -> `/api/events` (Bearer jika ada).
4. VM `LoadMemoriesAsync()` fetch `api/aggregated/memories` dari backend baru.
5. StatusBar update `name • ms • state`; no global cache invalidation — per-connection VM cache terpisah.

### Auth

- Desktop `GetClient()` -> `if (!string.IsNullOrWhiteSpace(conn.Token)) client.DefaultRequestHeaders.Authorization = Bearer`.
- Backend middleware: env empty -> pass; else compare constant-time; `/health` always 200 `{ authRequired:true/false }`.

### Multi-window

- `WindowFactory.Create(id)` -> new `MainWindow` + `MainViewModel(id)` (own `SseClient`, own poller, shared `Registry` singleton).
- Close window -> dispose its `SseClient`/`CancellationTokenSource` only; backend kill only from first local window `ProcessExit`.
- Tabs Fase 2: same window `TabControl`; tab switch = set `ActiveConnectionId` per tab, StatusBar reflects active tab's connection.

## 5. Error Handling & Auto-Reconnect

| Failure | Behavior |
|---|---|
| URL invalid / unreachable | Probe timeout 3s -> `unreachable` 🔴, StatusBar `reconnecting (retry in Ns)`, HealthPoller backoff 1s->2s->4s->8s->cap 30s + jitter 0.5s. Banner + Retry button (reset backoff to 1s). |
| Token wrong (401) | Middleware 401 -> desktop toast `🔒 unauthorized`, StatusBar `🔒 token salah`, stop auto-retry for that connection (no lockout), show Edit hint. Probe still runs but expects 401 -> maps to `authRequired`. |
| SSE disconnect (network/timeout) | SseClient reconnect loop same backoff (1s->cap 30s), resume `Last-Event-Id`. 401 -> stop, banner. Close window -> dispose loop. |
| Backend local spawn fail | Local entry 🔴 but remote stays usable; UI not blocked. |
| Settings corrupt | `ReadDocument` catch -> fallback `[local]`, log warning. |
| Concurrent window writes | Last-write-wins; connection list rarely mutates; save coalesced via Registry single writer. |

GET retry: `GetMemories`, `GetRuntimes`, `ListDir` auto-retry 1x after 500ms only for `HttpRequestException`/`Timeout`/`502`/`503`. Mutating `POST/PATCH/DELETE` never auto-retry (avoid double-write); fail -> toast; next health success auto-refetch.

## 6. Remote IP/DNS & TLS

- `ValidateUrl`: `Uri.TryCreate`, scheme `http`/`https` only, host non-empty. Allow IP literal (`127.0.0.1`, `192.168.x.x`, `::1`) and DNS (`eling.example.com`). Optional port, optional path prefix (e.g., `https://eling.example.com/api`). Reject `file://`, `ftp://`, empty host.
- `HttpClient`: default handler (no custom cert pinning YAGNI). HTTPS with self-signed -> user must trust OS store or use `http` via VPN; no `ServerCertificateCustomValidationCallback` bypass by default.
- Timeout 3s health, 10s API, 30s SSE. No proxy auto-detect override.

## 7. Sidecar Auto-Launch (local)

Only for `IsLocal == true`:

```
EnsureBackendRunningAsync:
  probe 4417 -> probe 4317 -> SpawnBackendAsync
SpawnBackendAsync:
  1. <DesktopBaseDir>/eling-backend.exe   (side-by-side deploy, priority 1)
  2. <repoRoot>/.bin/eling-backend.exe    (dev)
  3. dotnet run --project src/backend/Eling.Backend -p:ElingSkipDashboard=true
  ELING_DASHBOARD_PORT=4417, CreateNoWindow, kill on ProcessExit (first local window only)
```

Second local window reuse `ActivePort`, no double spawn.

## 8. Status Bar & Latency

- Bottom `DockPanel Dock.Bottom` in `MainWindow`: left `connection.Name • {ms}ms 🟢` / `reconnecting` / `offline`; right `auth ok` / `🔒 token salah` / `checking...`.
- `LatencyProbe`: `GET /health` every 5s when healthy, `Stopwatch` measure, update `ConnectionHealth.LatencyMs`. Color: <100ms 🟢, <300ms 🟡, else 🔴. Timeout/no response -> 🔴 `offline`.
- Per window polls its active connection; background windows poll theirs (cheap, /health).
- Tabs: StatusBar reflects active tab's connection.

## 9. Phasing

- **Fase 1** (this plan): ConnectionRegistry + DesktopSettingsStore migration + ElingApiClient token plumbing + ApiAuthMiddleware + Health authRequired + StatusBar + HealthPoller/LatencyProbe + Sse token + WindowFactory (free switch + per-window pin via `Ctrl+N`) + ConnectionManager CRUD + auto-reconnect (health/SSE/GET retry).
- **Fase 2**: Tabs in single window (`TabControl`, per-tab connectionId, shared registry). No registry/auth change.

## 10. Testing

Backend (`tests/Eling.Backend.Tests`, isolated `.bin-test`):
- `ApiAuthMiddlewareTests`: env empty -> 200 without header; env set -> 401 without/mismatch, 200 with correct Bearer; `/health` always 200 + `authRequired` flag.
- `HealthEndpointTests`: new field backward compatible.

Desktop (new `tests/Eling.Desktop.Tests` or `src/desktop/Eling.Desktop.Tests`):
- `ConnectionRegistryTests`: load/save roundtrip, migration from legacy settings, `ValidateUrl` reject/accept, health status mapping via fake HttpMessageHandler.
- `ElingApiClientTests`: Authorization header attached iff token set; baseUrl follows active connection (HttpClientFactory fake).
- `LatencyProbeTests`: color band thresholds + timeout -> 🔴.
- `WindowFactoryTests` (if feasible): factory creates window with correct connectionId.

Manual checklist (`validate-eling.ps1` extended):
- Run 2 backends (4417 no token, 4317 with `ELING_API_TOKEN=demo`), desktop switch dropdown, latency badge updates, 401 toast on wrong token, `Ctrl+N` new window pin to second backend, kill remote -> verify auto-reconnect recovery.

## 11. Risks & Mitigations

- Plain token in JSON -> document, warn in ConnectionManager; DPAPI follow-up.
- Health polling overhead -> only active connection polled, 5s interval.
- Last-write-wins settings race -> single Registry writer, save debounced 200ms.

## 12. Acceptance Criteria

- [ ] Desktop can add remote `http://ip:4417` / `https://dns` with name+token, persists across restart.
- [ ] Switch via dropdown reflects new backend data <1s, SSE reconnects.
- [ ] `Ctrl+N` opens new OS window pinned to chosen backend; 2 windows can show different backends simultaneously.
- [ ] Status bar shows `name • ms • state` with correct color; updates every 5s.
- [ ] Kill remote backend -> StatusBar `reconnecting`, auto-recovers when backend back without manual refresh.
- [ ] Wrong token -> 401 toast + StatusBar `🔒 token salah`, no infinite retry.
- [ ] Local sidecar launches from side-by-side exe when no backend running.
- [ ] Remote IP/DNS URL accepted, `file://` rejected.
- [ ] Existing single-backend settings auto-migrate to Connections list.

## 13. Open Decisions Resolved

- No auth dulu -> revised to auth on (Bearer per connection, `ELING_API_TOKEN` opt-in).
- Taskbar -> clarified to in-app status bar.
- Remote addressing -> IP or DNS both supported.
