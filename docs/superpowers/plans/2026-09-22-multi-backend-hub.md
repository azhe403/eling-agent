# Multi-Backend Desktop Hub — Implementation Plan (Fase 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn Eling Desktop into a multi-backend hub: connection registry (local + remote IP/DNS), Bearer auth opt-in, free-switch dropdown, per-window pin via Ctrl+N, status bar with latency, auto-reconnect (health/SSE/GET), ConnectionManager CRUD. Spec: `docs/superpowers/specs/2026-09-22-multi-backend-hub-design.md`.

**Architecture:** `ConnectionRegistry` singleton (sourced from extended `DesktopSettingsStore.SettingsDocument`) holds `BackendConnection[] { id, name, baseUrl, token?, isLocal }`. Per-window `MainViewModel` owns `ActiveConnectionId`; `ElingApiClient`/`SseClient` resolve baseUrl+token from a `Func<BackendConnection>` provider per call. Backend gets opt-in `ApiAuthMiddleware` (env `ELING_API_TOKEN`) + `/health.authRequired`. Fase 2 (tabs) — separate plan, NOT here.

**Tech Stack:** .NET 10, Avalonia 12.1.2 + ReactiveUI, xUnit (`tests/Eling.Backend.Tests`, `tests/Eling.Desktop.Tests` — both exist), ASP.NET Core minimal APIs.

## Global Constraints

- `ELING_API_TOKEN` empty → everything passes (zero change for existing local setups).
- No auto-retry on POST/PATCH/DELETE. 401 → stop retry, show state, wait manual fix.
- Settings JSON backward-compatible: legacy `BackendUrl`-only doc must migrate to `Connections[]` with default local.
- Remote = URL only (IP literal or DNS); `isLocal=false` never spawns.
- Commit ONLY after full implementation completes (user instruction: "commit later after full implementation").

---

### Task 1: Backend — ApiAuthMiddleware (TDD)

**Files:**
- Create: `src/backend/Eling.Backend/Bootstrap/ApiAuthMiddleware.cs`
- Edit: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` (register before `Map*`)
- Create test: `tests/Eling.Backend.Tests/Bootstrap/ApiAuthMiddlewareTests.cs`

**Interfaces:**
- `ApiAuthMiddleware(RequestDelegate next)`: reads `Environment.GetEnvironmentVariable("ELING_API_TOKEN")` per-request (so tests can flip env). Path starting `/api/` (except skip list) requires `Authorization: Bearer <token>` when env non-empty; mismatch/missing → 401 JSON `{"error":"unauthorized"}`. `/health` always passes.

- [ ] **Step 1: Write failing tests**

```csharp
[Theory]
[InlineData(null, "/api/aggregated/memories", 200)]            // no env -> pass
[InlineData("secret", "/api/aggregated/memories", 401)]         // env, no header
[InlineData("secret", "/api/aggregated/memories", 200)]         // env + correct Bearer
[InlineData("secret", "/api/aggregated/memories", 401)]         // env + wrong Bearer
[InlineData("secret", "/health", 200)]                          // /health always 200
public async Task Middleware_enforces_token(string? env, string path, int expected)
```
Build header inside test: `Bearer secret` when expected==200 && env!=null. Use `WebApplication`-style or direct `DefaultHttpContext` + `ApiAuthMiddleware.Invoke`.

- [ ] **Step 2: Run** `dotnet test tests/Eling.Backend.Tests --filter ApiAuthMiddleware` → red
- [ ] **Step 3: Implement** `ApiAuthMiddleware`:

```csharp
public sealed class ApiAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var token = Environment.GetEnvironmentVariable("ELING_API_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || !path.StartsWith("/api/", StringComparison.Ordinal))
        { await next(ctx); return; }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.Equals($"Bearer {token}", StringComparison.Ordinal))
        { await next(ctx); return; }

        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
    }
}
```
- [ ] **Step 4: Register** in `DashboardRoutes` pipeline before endpoint maps: `app.UseMiddleware<ApiAuthMiddleware>();` (guard: only when app built — follow existing registration site for dashboard endpoints).
- [ ] **Step 5: Run test** → green. Commit allowed ONLY at end of plan.

---

### Task 2: Backend — /health returns authRequired

**Files:**
- Edit: `src/backend/Eling.Backend/Endpoints/HealthEndpoint.cs`
- Edit test: `tests/Eling.Backend.Tests/Endpoints/HealthEndpointTests.cs` (or new case)

- [ ] **Step 1: Failing test** — response JSON contains `authRequired` = `!string.IsNullOrWhiteSpace(env ELING_API_TOKEN)` (true/false cases).
- [ ] **Step 2: Implement** — add `authRequired` field to health payload.
- [ ] **Step 3: Run** → green.

---

### Task 3: Desktop — BackendConnection model + ValidateUrl

**Files:**
- Create: `src/desktop/Eling.Desktop/Models/BackendConnection.cs`
- Create: `src/desktop/Eling.Desktop/Services/ConnectionUrlValidator.cs` (static, one method — keeps model file dumb)
- Create test: `tests/Eling.Desktop.Tests/ConnectionUrlValidatorTests.cs`

**Interfaces:**
```csharp
public sealed record BackendConnection(string Id, string Name, string BaseUrl, string? Token, bool IsLocal);
public static class ConnectionUrlValidator
{
    public static bool TryValidate(string url, out string normalized, out string? error);
}
```
Rules: `Uri.TryCreate`, scheme http/https only, non-empty host (IP literal or DNS), normalize: trim + trim trailing `/`. Reject `file://`, `ftp://`, empty host.

- [ ] **Step 1: Failing tests**: accept `http://127.0.0.1:4417`, `https://eling.example.com/api`, `[::1]:4417` form; reject `file:///tmp`, `ftp://x`, ``, `not a url`.
- [ ] **Step 2: Implement** → green.

---

### Task 4: Desktop — DesktopSettingsStore migration (Connections)

**Files:**
- Edit: `src/desktop/Eling.Desktop/Services/DesktopSettingsStore.cs`
- Create test: `tests/Eling.Desktop.Tests/DesktopSettingsStoreTests.cs`

**Interfaces:**
- `SettingsDocument` gains `List<BackendConnection>? Connections` + `string? ActiveConnectionId`.
- New methods: `GetConnections()` (migrate-on-read: legacy `BackendUrl` set / empty list → return `[{ "local", url ?? 4417 default, IsLocal=true }]`), `SaveConnections(list, activeId)`.
- Legacy `GetBackendUrl()`/`SetBackendUrl()` keep working (map to first/active connection).

- [ ] **Step 1: Failing tests**: legacy doc (only BackendUrl) → GetConnections returns 1 local entry with that URL; save roundtrip; corrupt JSON → default local (matching existing catch pattern).
- [ ] **Step 2: Implement** → green.

---

### Task 5: Desktop — ConnectionRegistry

**Files:**
- Create: `src/desktop/Eling.Desktop/Services/ConnectionRegistry.cs`
- Create test: `tests/Eling.Desktop.Tests/ConnectionRegistryTests.cs`

**Interfaces:**
```csharp
public sealed class ConnectionRegistry(DesktopSettingsStore store, ILogger<ConnectionRegistry> log)
{
    public IReadOnlyList<BackendConnection> Connections { get; }
    public string ActiveConnectionId { get; }
    public event Action? ConnectionsChanged;
    public BackendConnection GetActive();
    public BackendConnection? GetById(string id);
    public Task AddAsync(string name, string url, string? token, CancellationToken ct = default); // probe first
    public Task UpdateAsync(BackendConnection conn, CancellationToken ct = default);
    public void Remove(string id);              // never remove last; refuse removing active unless switched
    public void SetActive(string id);           // persist
    public Task<ConnectionHealth> ProbeAsync(BackendConnection conn, CancellationToken ct = default);
}
public sealed record ConnectionHealth(ConnectionStatus Status, int? LatencyMs, bool AuthRequired, DateTimeOffset ProbedAt);
public enum ConnectionStatus { Unknown, Reachable, AuthRequired, Unreachable }
```
Probe: `GET {baseUrl}/health`, Stopwatch, 3s timeout; map `authRequired:true` → `AuthRequired`; HttpRequestException/timeout → `Unreachable`.

- [ ] **Step 1: Failing tests** (fake `HttpMessageHandler` via injectable `Func<BackendConnection, HttpClient>` or small `IHealthProber` seam — pick smallest): add persists; probe latency recorded; 401 from API probe path → AuthRequired; unreachable → Unreachable; SetActive persists; Remove last refused.
- [ ] **Step 2: Implement** → green.

---

### Task 6: Desktop — ElingApiClient token + provider + GET retry

**Files:**
- Edit: `src/desktop/Eling.Desktop/Services/ElingApiClient.cs`
- Create test: `tests/Eling.Desktop.Tests/ElingApiClientTests.cs`

**Changes:**
- New ctor: `ElingApiClient(Func<BackendConnection> connectionProvider, ILogger<ElingApiClient> logger)` (keep old ctors working during transition, delete old after Task 8 wiring).
- `GetClient()`: BaseAddress from `connection.BaseUrl`; if `!string.IsNullOrWhiteSpace(connection.Token)` set `Authorization: Bearer {token}`.
- GET helper wraps `HttpRequestException` / `TaskCanceledException` / status 502/503 → single retry after 500ms. Mutating methods: no retry.

- [ ] **Step 1: Failing tests**: token set → outgoing request has `Authorization: Bearer t`; token null → no header; baseUrl follows provider; GET 503-then-200 succeeds (fake handler); POST 503-then-200 fails (no retry).
- [ ] **Step 2: Implement** → green.

---

### Task 7: Desktop — SseClient token + auto-reconnect backoff

**Files:**
- Edit: `src/desktop/Eling.Desktop/Services/SseClient.cs`
- Create test: `tests/Eling.Desktop.Tests/SseClientTests.cs` (extract loop decision logic to testable static/class if handler-heavy: `ReconnectPolicy`)

**Changes:**
- `Connect(string baseUrl, string? token, CancellationToken)` — Bearer header if token.
- Reconnect loop: backoff 1s→2s→4s→8s→cap 30s + jitter 0.5s on network drop; on 401 stop permanently (surface event `AuthFailed`).
- `Disconnect()` cancels loop (existing dispose pattern).

- [ ] **Step 1: Failing tests** on `ReconnectPolicy`: sequence 1,2,4,8,16,30,30…; 401 → no further delay (terminal).
- [ ] **Step 2: Implement** → green.

---

### Task 8: Desktop — WindowFactory + MainViewModel wiring (free switch + pin)

**Files:**
- Create: `src/desktop/Eling.Desktop/Services/WindowFactory.cs`
- Edit: `src/desktop/Eling.Desktop/ViewModels/MainViewModel.cs`
- Edit: `src/desktop/Eling.Desktop/App.axaml.cs` (DI: register ConnectionRegistry singleton, WindowFactory; first window seeds from ActiveConnectionId)
- Edit: `src/desktop/Eling.Desktop/Views/MainWindow.axaml` (header dropdown + Ctrl+N)
- Edit: `src/desktop/Eling.Desktop/Services/BackendSupervisor.cs` (only called when active connection `IsLocal`)

**Interfaces:**
```csharp
public sealed class WindowFactory(IServiceProvider sp, ConnectionRegistry registry)
{
    public Window Create(string? initialConnectionId = null);
}
```
- MainViewModel: add `ObservableCollection<BackendConnection> Connections`, `ActiveConnectionId`, `SwitchConnection(id)` (SetActive → reconnect SSE → reload memories), `OpenNewWindowCommand` (Ctrl+N → `WindowFactory.Create(ActiveConnectionId)`).
- MainWindow template: `ComboBox` bound to Connections (DisplayMemberBinding = Name), `HotKey="Ctrl+N"`.

- [ ] **Step 1: Implement wiring** (desktop UI — manual verify in Task 11; unit tests only for `SwitchConnection` state transitions in MainViewModel if constructible headless, else skip with comment).
- [ ] **Step 2: `dotnet build src/desktop/Eling.Desktop`** → 0 errors.

---

### Task 9: Desktop — HealthPoller + LatencyProbe + Status bar

**Files:**
- Create: `src/desktop/Eling.Desktop/Services/ConnectionHealthMonitor.cs` (combines poll 5s + backoff; replaces ad-hoc probe in switch)
- Create: `src/desktop/Eling.Desktop/ViewModels/StatusBarViewModel.cs` (or fold into MainViewModel — choose fold if <30 lines, else separate)
- Edit: `src/desktop/Eling.Desktop/Views/MainWindow.axaml` (DockPanel Dock.Bottom StatusBar: `{Name} • {ms}ms {dot} | {state}`)
- Create test: `tests/Eling.Desktop.Tests/ConnectionHealthMonitorTests.cs`

**Interfaces:**
```csharp
public sealed class ConnectionHealthMonitor
{
    // polls active connection /health every 5s healthy; backoff 1s→30s jitter on failure; 401/authRequired → status AuthRequired (no backoff loop abuse)
    public ConnectionHealth Current { get; }
    public event Action<ConnectionHealth>? HealthChanged;
    public void SwitchTo(BackendConnection conn); // used by SwitchConnection
    public void RetryNow(); // reset backoff to 1s (manual button)
}
```
Color: <100ms 🟢, <300ms 🟡, else 🔴; state text `online`/`reconnecting (retry in Ns…)`/`offline`/`🔒 invalid token`.

- [ ] **Step 1: Failing tests** (fake clock or injectable delay): healthy → 5s cadence; failure → backoff sequence; RetryNow resets; AuthRequired terminal-ish (no retry when probe returns authRequired=true from /health... actually /health is 200 always — detection of wrong token comes from API 401: monitor exposes `MarkAuthFailed()` called by api client on 401).
- [ ] **Step 2: Implement + XAML** → green + build.

---

### Task 10: Desktop — ConnectionManager dialog (CRUD)

**Files:**
- Create: `src/desktop/Eling.Desktop/Views/ConnectionManagerView.axaml` + `.cs`
- Create: `src/desktop/Eling.Desktop/ViewModels/ConnectionManagerViewModel.cs`
- Edit: `src/desktop/Eling.Desktop/Views/MainWindow.axaml` (Manage button in header opens dialog)

**Behavior:** list connections (name, url, status dot), Add/Edit form (Name, BaseUrl, Token password box, Test button → registry.ProbeAsync shows result), Delete (disabled for last/active-without-switch), Save → registry Add/Update/Remove → `ConnectionsChanged` refreshes dropdowns.

- [ ] **Step 1: Implement VM logic tests** (probe result mapping, delete guards) in `ConnectionManagerViewModelTests.cs`.
- [ ] **Step 2: XAML + wiring** → build green.

---

### Task 11: Validation

- [ ] `dotnet test tests/Eling.Backend.Tests` → all green
- [ ] `dotnet test tests/Eling.Desktop.Tests` → all green
- [ ] `dotnet build src/desktop/Eling.Desktop` + `dotnet build src/backend/Eling.Backend` → 0 errors
- [ ] Manual: run backend on 4417 (no token) + second instance 4317 with `ELING_API_TOKEN=demo`; desktop: add both connections, switch dropdown (<1s, memories reload), wrong-token connection → 401 → `🔒 invalid token`, kill second backend → status `reconnecting` → restart → auto `online`, Ctrl+N second window pin different backend, latency ms updates ≤5s.
- [ ] Run `scripts/validate-eling.ps1` if applicable.

---

### Task 12: Commit (user-gated)

- [ ] Single commit after ALL validation green: `feat(desktop+backend): multi-backend hub — connection registry, Bearer auth, status bar latency, auto-reconnect` (+ body listing spec link). Do NOT commit earlier (user instruction).

---

**Out of scope (Fase 2 plan later):** tabs in single window (`TabControl`, shared registry — UI only).
