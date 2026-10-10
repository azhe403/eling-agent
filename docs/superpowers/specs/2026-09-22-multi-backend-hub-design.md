# Multi-Backend Desktop Hub — Design Specification

- Date: 2026-09-22
- Status: Approved (Phased)
- Scope: `src/desktop/Eling.Desktop` (hub) + `src/backend/Eling.Backend` (opt-in auth) + tests
- Phasing: Phase 1 = multi-backend + auth + 2 window modes (free switch + per-window pin) + status bar + auto-reconnect. Phase 2 = tabs in a single window.

## 1. Context & Goals

Currently, Eling Desktop is single-backend: `BackendSupervisor` probes `127.0.0.1:4417/4317` and spawns `eling-backend.exe` / `dotnet run`. Users require a hub capable of managing multiple backends (local + remote via IP or DNS), instant switching, multi-window targeting different backends, status bar latency display, robust auto-reconnect on network drops, Bearer token authentication, and flexible remote addressing (LAN, VPS, tunnel).

Goals:
- Turn Desktop into a hub: add/edit/remove connections `{ name, baseUrl, token? }`, switch in <1s, support multi-window and tabs concurrently.
- Flexible remote addressing: support `http://host:port` and `https://dns/path`, URL validation, and IPv4/IPv6 literal and DNS resolution.
- Opt-in authentication: local without token remains seamless; remote can enforce `Authorization: Bearer <token>` when backend configures `ELING_API_TOKEN`.
- Per-window status bar: display `name • 42ms 🟢 | online/reconnecting/🔒`.
- Resilient auto-reconnect: handle health poll, SSE streams, and idempotent GET retries gracefully.
- Local sidecar auto-launch: desktop automatically launches `eling-backend.exe` side-by-side if not already active.

Non-goals (YAGNI):
- No DPAPI/Keychain in Phase 1 — tokens stored in `desktop-settings.json` (reserved for subsequent security hardening).
- No complex backend discovery service — connections configured explicitly via URL and token.
- No dedicated database for connection registry — local JSON configuration suffices.
- No mTLS or OAuth — Bearer tokens provide sufficient transport authentication.

## 2. Architecture

### Backend (Opt-in, Non-breaking Local)

```
Kestrel pipeline: ApiAuthMiddleware -> DashboardRoutes -> Endpoints
                    |                      |
                    +-> skip /health       +-> /health returns { authRequired }
                    +-> if ELING_API_TOKEN empty: pass
                    +-> else check Authorization: Bearer <token> for /api/* -> 401 if mismatch
```

- Single-binary port ownership remains intact: port probe wins, peer instances bypass Kestrel (existing `DashboardPort` / `HttpLoop`).
- Zero changes to `Eling.Core` memory domain.

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

- `ConnectionRegistry` is a singleton; each window tracks an `ActiveConnectionId` in its ViewModel.
- `ElingApiClient` factory `CreateFor(connectionId)`: `GetClient()` injects Authorization header when token is present, reusing all existing API methods.
- `isLocal` flag: only local entries trigger `BackendSupervisor` process spawning; remote entries skip process management.

## 3. Components

### Backend — New and Modified

- `src/backend/Eling.Backend/Bootstrap/ApiAuthMiddleware.cs` (new): reads `ELING_API_TOKEN` (env, trimmed), skips `/health`, validates `Authorization: Bearer <token>` for `/api/*`, returns 401 JSON `{ error: "unauthorized" }` on mismatch.
- `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` (edit): registers middleware before `Map*`.
- `src/backend/Eling.Backend/Endpoints/HealthEndpoint.cs` (edit): adds `authRequired: bool` to response payload.

### Desktop — New Components

- `src/desktop/Eling.Desktop/Models/BackendConnection.cs`: `record BackendConnection(string Id /* ulid */, string Name, string BaseUrl, string? Token, bool IsLocal)`.
- `src/desktop/Eling.Desktop/Services/ConnectionRegistry.cs`: loads/saves via `DesktopSettingsStore`, validates URL via `ConnectionUrlValidator` (only `http://` / `https://`, rejects `file://`), probes `/health` asynchronously (3s timeout, Stopwatch), tracks `ConnectionHealth { Status, LatencyMs, LastProbeAt, AuthRequired }`, raises `ConnectionsChanged`.
- `src/desktop/Eling.Desktop/Services/WindowFactory.cs`: `Create(string? initialConnectionId)` -> instantiates `MainWindow` + `MainViewModel`.
- `src/desktop/Eling.Desktop/Services/LatencyProbe.cs` (or inside Registry): polls `/health` per active connection with a 5s interval and backoff.
- `src/desktop/Eling.Desktop/Views/ConnectionManagerView.axaml` + `.cs` + `ViewModels/ConnectionManagerViewModel.cs`: connection CRUD dialog (name/url/token, Test button -> `GET /health` + `GET /api/memories?limit=1`).
- `src/desktop/Eling.Desktop/Views/StatusBarView.axaml` (or inline in MainWindow): bottom status bar component.

### Desktop — Updates to Existing Files

- `Services/DesktopSettingsStore.cs`: extends `SettingsDocument` -> `record SettingsDocument(string? BackendUrl, WindowBounds? Window, List<BackendConnection>? Connections, string? ActiveConnectionId)`. Migration: if `Connections` is null or empty, inject default local entry `[ { local, http://127.0.0.1:4417, isLocal: true } ]`. Retain `BackendUrl` for backward compatibility, syncing with the active connection.
- `Services/ElingApiClient.cs`: constructor accepts `Func<BackendConnection>` provider. `GetClient()` sets `Authorization` header when token is present. Avoids method duplication.
- `Services/SseClient.cs`: accepts `baseUrl + token` per connection, attaches Bearer token if present, wires `Last-Event-Id` resumption.
- `Services/BackendSupervisor.cs`: executes only for `IsLocal == true`; remote instances bypass process spawn. Secondary local windows reuse existing `ActivePort`.
- `ViewModels/MainViewModel.cs` + `Views/MainWindow.axaml`: header dropdown (ItemsSource bound to `Registry.Connections`), status indicators 🟢/🟡/🔴/🔒, Manage button, `Ctrl+N` new window command. Phase 2: replaces content host with `TabControl` (each `TabItem` represents a connection, reusing the ViewModel template with distinct connection IDs).
- `App.axaml.cs`: registers `ConnectionRegistry` singleton, `WindowFactory`, and wires dependency injection.

## 4. Data Flow

### Boot Sequence

1. `App.OnFrameworkInitializationCompleted` -> `DesktopSettingsStore.ReadDocument()` -> `ConnectionRegistry.Load()`.
2. If no `local` entry exists, inject the default local backend connection.
3. If the active connection has `IsLocal == true`, invoke `BackendSupervisor.EnsureBackendRunningAsync()` (probes ports 4417/4317 and spawns side-by-side binary if needed).
4. The initial `MainWindow` binds to `ActiveConnectionId` (fallback to local).
5. Subscribe to `Registry.ConnectionsChanged` to keep dropdowns reactive.
6. Start `HealthPoller` + `LatencyProbe` for the active connection, and initiate `SseClient.Connect(active)`.

### Connection Switching (Dropdown / Tabs / New Window)

1. User selects a connection -> ViewModel updates `ActiveConnectionId = id` -> `Registry.SetActive(id)` persists selection to JSON.
2. `ElingApiClient` targets the new `baseUrl` and token on subsequent calls.
3. `SseClient.Disconnect()` followed by `Connect(new baseUrl, token)` against `/api/events`.
4. ViewModel invokes `LoadMemoriesAsync()` to fetch `api/aggregated/memories` from the newly active backend.
5. StatusBar updates to reflect `name • ms • state`. Separate ViewModel instances maintain isolated cache per connection.

### Authentication Flow

- Desktop `GetClient()`: `if (!string.IsNullOrWhiteSpace(conn.Token)) client.DefaultRequestHeaders.Authorization = Bearer`.
- Backend middleware: if environment variable is empty, pass through; otherwise compare constant-time; `/health` always returns 200 `{ authRequired: true/false }`.

### Multi-Window Handling

- `WindowFactory.Create(id)` spawns a new `MainWindow` + `MainViewModel(id)` (independent `SseClient`, dedicated poller, shared `Registry` singleton).
- Window close: disposes only that window's `SseClient` and `CancellationTokenSource`. Backend process termination occurs only via `ProcessExit` on the primary local window.
- Phase 2 Tabs: single-window `TabControl`; tab switching sets `ActiveConnectionId` per tab, and the StatusBar updates to reflect the active tab.

## 5. Error Handling & Auto-Reconnect

| Failure Scenario | Behavior |
|---|---|
| URL invalid or unreachable | Health probe times out (3s) -> status becomes `unreachable` 🔴; StatusBar displays `reconnecting (retry in Ns)`; HealthPoller applies exponential backoff (1s -> 2s -> 4s -> 8s -> capped at 30s + 0.5s jitter). Displays notification banner with manual Retry button (which resets backoff to 1s). |
| Invalid token (401 Unauthorized) | Middleware returns 401 -> desktop displays toast `🔒 unauthorized`; StatusBar displays `🔒 invalid token`; halts automated retries for that connection to avoid spamming the log; displays Edit Connection prompt. Health probe continues and maps 401 response to `authRequired`. |
| SSE disconnect (network/timeout) | SseClient reconnection loop applies same backoff policy (1s -> capped at 30s) and sends `Last-Event-Id` on reconnect. On 401, reconnection stops and triggers an alert banner. Disposing the window terminates the loop. |
| Local backend launch failure | Local entry is flagged 🔴 while remote connections remain fully operational; UI thread is never blocked. |
| Settings file corruption | `ReadDocument` catches error, falls back to default `[local]` entry, and logs a warning. |
| Concurrent window writes | Last-write-wins; connection list changes infrequently; saves are debounced and coordinated via single-writer Registry. |

GET Retry: `GetMemories`, `GetRuntimes`, and `ListDir` automatically retry once after a 500ms delay on `HttpRequestException`, `Timeout`, or status 502/503. Mutating operations (`POST`, `PATCH`, `DELETE`) never auto-retry to prevent duplicate side effects; failures prompt a UI toast, and memory lists refresh automatically upon the next successful health probe.

## 6. Remote Addressing & Transport Security

- `ValidateUrl`: uses `Uri.TryCreate`, enforces `http` or `https` schemes only, and requires non-empty hosts. Supports IPv4/IPv6 literals (`127.0.0.1`, `192.168.x.x`, `[::1]`) and DNS hostnames (`eling.example.com`). Supports optional ports and path prefixes (e.g. `https://eling.example.com/api`). Rejects `file://`, `ftp://`, and empty hosts.
- `HttpClient`: standard handler configuration without custom certificate pinning. Self-signed HTTPS certificates require trusting the host OS certificate store or tunneling over VPN; `ServerCertificateCustomValidationCallback` bypasses are disabled by default.
- Timeouts: 3s for health checks, 10s for REST APIs, 30s for SSE streams.

## 7. Local Sidecar Auto-Launch

Applies only when `IsLocal == true`:

```
EnsureBackendRunningAsync:
  probe 4417 -> probe 4317 -> SpawnBackendAsync
SpawnBackendAsync:
  1. <DesktopBaseDir>/eling-backend.exe   (side-by-side deployment, priority 1)
  2. <repoRoot>/.bin/eling-backend.exe    (local development)
  3. dotnet run --project src/backend/Eling.Backend -p:ElingSkipDashboard=true
  ELING_DASHBOARD_PORT=4417, CreateNoWindow, terminate on ProcessExit (primary local window only)
```

Secondary local windows reuse the active port and avoid duplicate process spawns.

## 8. Status Bar & Latency Metrics

- Bottom `DockPanel Dock.Bottom` in `MainWindow`: left panel displays `{connection.Name} • {ms}ms 🟢` / `reconnecting` / `offline`; right panel displays `auth ok` / `🔒 invalid token` / `checking...`.
- `LatencyProbe`: executes `GET /health` every 5s on healthy connections, measures round-trip time via `Stopwatch`, and updates `ConnectionHealth.LatencyMs`. Color thresholds: <100ms 🟢, <300ms 🟡, otherwise 🔴. Unreachable or timed-out hosts display 🔴 `offline`.
- Active window polls its active connection; background windows poll theirs periodically.
- Phase 2: StatusBar reflects the selected tab's connection.

## 9. Phasing Plan

- **Phase 1** (Current Plan): `ConnectionRegistry` + `DesktopSettingsStore` migration + `ElingApiClient` token injection + `ApiAuthMiddleware` + `/health.authRequired` + StatusBar + HealthPoller/LatencyProbe + SSE token support + `WindowFactory` (free switch + per-window pinning via `Ctrl+N`) + ConnectionManager CRUD dialog + resilient auto-reconnect (health/SSE/GET retries).
- **Phase 2**: Tabs within a single window (`TabControl`, per-tab connection ID, shared registry). No backend or authentication changes required.

## 10. Testing Strategy

Backend (`tests/Eling.Backend.Tests`, isolated `.bin-test`):
- `ApiAuthMiddlewareTests`: empty environment variable -> 200 without header; environment variable set -> 401 without or with invalid header, 200 with matching Bearer token; `/health` always returns 200 with `authRequired` flag.
- `HealthEndpointTests`: verifies backward compatibility of health payload.

Desktop (`tests/Eling.Desktop.Tests`):
- `ConnectionRegistryTests`: load/save round-trip, migration from legacy settings, `ValidateUrl` validation rules, and health status mapping via mock HttpMessageHandler.
- `ElingApiClientTests`: Authorization header attached when token is set; BaseAddress tracks active connection.
- `LatencyProbeTests`: verifies latency color thresholds and timeout mapping to 🔴.
- `WindowFactoryTests`: verifies window creation with specified initial connection ID.

Manual Verification Checklist:
- Run two backend instances (port 4417 without token, port 4317 with `ELING_API_TOKEN=demo`). In desktop: switch dropdown, verify latency indicator, verify 401 toast on invalid token, open second window with `Ctrl+N` pinned to second backend, terminate remote backend to verify auto-reconnect recovery.

## 11. Risks & Mitigations

- Plaintext tokens in JSON: document clearly, display warning banner in ConnectionManager; DPAPI encryption scheduled for follow-up iteration.
- Health polling overhead: only active connections are polled at 5-second intervals.
- Concurrent settings write race: single-writer registry architecture with debounced disk persistence.

## 12. Acceptance Criteria

- [ ] Desktop supports adding remote `http://ip:4417` or `https://dns` backends with name and token, persisting across restarts.
- [ ] Dropdown connection switch reflects new backend data in <1s and reconnects SSE.
- [ ] `Ctrl+N` opens a new OS window pinned to the chosen backend; multiple windows can view distinct backends simultaneously.
- [ ] Status bar displays `name • ms • state` with accurate latency colors and 5s polling cadence.
- [ ] Terminating a remote backend triggers `reconnecting` state, recovering automatically once the backend returns.
- [ ] Invalid tokens produce 401 toasts and `🔒 invalid token` status without infinite retry loops.
- [ ] Local sidecar launches from side-by-side executable when no backend is running.
- [ ] Remote IP/DNS URLs accepted; `file://` rejected.
- [ ] Existing single-backend settings migrate smoothly to the `Connections` collection.

## 13. Resolved Decisions

- Authentication: opt-in Bearer token per connection via `ELING_API_TOKEN`.
- Status bar: integrated in-app status bar docked at bottom of main window.
- Remote addressing: supports both IP literals and DNS domain names.
