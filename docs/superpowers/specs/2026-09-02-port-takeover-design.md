# In-Place Port Takeover Design

> Status: Draft (pending user review).
> Supersedes: none.
> Plan: to be created via writing-plans after approval.
> Tracker: created 2026-09-02.

## 1. Purpose

Define an **in-place port takeover** capability for Eling: when an owner backend
dies, a surviving MCP-only peer detects the freed dashboard port and promotes
itself to owner without a process restart — maintaining its MCP stdio connection
the whole time.

This replaces the current behavior where a peer that loses the initial port race
never re-checks and stays MCP-only forever, leaving the dashboard permanently
unreachable after the owner exits.

## 2. Scope

### 2.1 In-Scope

- `PortMonitor` helper in `McpHostArchitecture.cs` (port-free polling).
- Refactored `HttpLoop.RunAsync` peer path: poll loop + rebuild as owner.
- New env var `ELING_TAKEOVER_MS` (int ms, default 3000) for poll interval.
- Flipped verification test `Owner_exit_leaves_port_free_peer_does_not_take_over`
  → `Peer_takes_over_port_when_owner_exits` (asserts takeover succeeds).
- Fast `ELING_TEST_TAKEOVER_MS` timing var in `TestTimingEnv`.
- Fix 4 stale lifecycle tests for merged single-binary architecture:
  - `First_runtime_starts_dashboard_and_registers_itself` (dashboardPid == runtime.Id now).
  - `Second_runtime_reuses_the_same_dashboard` (registry `Alive()` filtering alignment).
  - `Disconnect_removes_only_that_runtime_and_keeps_dashboard_alive` (registry filtering).
  - `No_dashboard_flag_skips_dashboard_startup` (remove — `--no-dashboard` no longer exists).

### 2.2 Out-of-Scope

- Multi-process orchestration / supervisor pattern.
- Event-driven port-free notifications (YAGNI — poll at 3–5s is sufficient).
- Changes to `RuntimeRegistry`, `DashboardPort`, `RuntimeSelfRegistration`, or
  `DashboardRoutes` (all remain as-is; the promoted process rebuilds a fresh
  WebApplication through the existing `BuildWebApplication` path).
- Frontend architecture changes (4427 spawn/unspawn stays the same; only the
  promotion path triggers a respawn in dev mode).

## 3. Architecture

### 3.1 Current Flow

```
HttpLoop.RunAsync:
  while (!cancelled):
    app = BuildWebApplication(...)
    if IsLoopbackListening(port):
      ownerMode = false
      # peer: no Kestrel, no routes
      app.RunAsync(ct)  ← blocks forever, never re-checks
    else:
      ownerMode = true
      # owner: Kestrel binds, serves HTTP, registers, spawns frontend
      app.RunAsync(ct)  ← blocks until cancellation
    # finally: dispose app
```

**Problem**: The peer path blocks in `app.RunAsync` forever and never re-evaluates
port availability.

### 3.2 Proposed Flow

```
PortMonitor.WaitForPortFree(port, interval, ct):
  while (!cancelled && IsLoopbackListening(port)):
    delay(interval)  ← resumable via CancellationToken
  # returns when port is free

HttpLoop.RunAsync:
  while (!cancelled):
    app = BuildWebApplication(...)
    if ownerMode = false:
      # PEER PATH: poll instead of blocking forever
      try:
        PortMonitor.WaitForPortFree(port, ELING_TAKEOVER_MS, ct)
        log "Port freed; promoting peer to owner"
      finally:
        app.Dispose()  ← release the non-listening app
      # continue loop → rebuild → now sees port free → ownerMode=true
    else:
      # OWNER PATH: unchanged
      app.RunAsync(ct)
    # finally: dispose app
```

**Key insight**: The loop already does everything needed. The only change is:
replace the peer's blocking `app.RunAsync` with a poll wait, then `continue`.
The next iteration's `BuildWebApplication` will see the free port and configure
the app as owner (routes, self-registration, frontend spawn).

### 3.3 Config

| Env var | Default | Purpose |
|---|---|---|
| `ELING_TAKEOVER_MS` | 3000 | Peer poll interval (ms) before re-checking port |
| `ELING_TEST_TAKEOVER_MS` | — | Test-only override (ms) for fast lifecycle tests |

Resolves via `DashboardPort.Resolve()` pattern: parse int from env, fallback to
default. Included in `TestTimingEnv` as `["ELING_TAKEOVER_MS"] = "300"`.

### 3.4 Race Handling

When two peers detect the port free simultaneously:
- Both loop back and rebuild as owner.
- One wins the Kestrel bind; the other gets `SocketException.AddressAlreadyInUse`.
- The existing catch in `RunAsync` logs the failure, applies jitter delay, and
  re-enters peer mode (next `BuildWebApplication` sees port taken → `ownerMode=false`
  → poll resumes).
- No new logic needed: the existing bind-error catch already handles this.

### 3.5 Shutdown

On `ApplicationStopping` (e.g. SIGTERM, Ctrl+C):
- `cancellationToken` fires → `WaitForPortFree` returns immediately.
- `RunAsync` catches `OperationCanceledException` → returns 0 → process exits.
- Same as current behavior: clean shutdown is not affected.

## 4. Data Flow (Promotion Sequence)

```
[Peer process, MCP stdio alive]
  ↓ port becomes free (owner died)
PortMonitor detects IsLoopbackListening → false
  ↓
Dispose non-listening peer app
  ↓
Loop: BuildWebApplication → IsLoopbackListening → false → ownerMode=true
  ↓
Kestrel binds 127.0.0.1:{port}
  ↓
DashboardRoutes.Map(app)       # REST endpoints, SSE, memory routes
RuntimeSelfRegistration.Wire() # registers in RuntimeRegistry
  ↓ (dev mode)
FrontendDevSpawner.TrySpawnPnpmFrontend()  # respawn 4427
  ↓
app.RunAsync(ct)  # blocks until shutdown
```

**MCP stays alive throughout**: the GenericHost (MCP stdio transport) runs
separately from `HttpLoop.RunAsync` in `Program.cs` — it is never stopped during
promotion.

## 5. Tests

### 5.1 Updated Verification Test

**Before**: `Owner_exit_leaves_port_free_peer_does_not_take_over` asserts `Assert.Null(tookOverPid)`
— documents the gap, confirms no takeover.

**After**: Rename to `Peer_takes_over_port_when_owner_exits`. After owner shuts down,
assert that the peer now serves HTTP within `ELING_TAKEOVER_MS + margin`:
```csharp
var tookOverPid = await TestProcesses.WaitForDashboardPidAsync(_client, TimeSpan.FromSeconds(10));
Assert.NotNull(tookOverPid);
Assert.Equal(second.Id, takenOverPid!.Value);  // the peer promoted, not a ghost
```

**Timing**: Set `ELING_TEST_TAKEOVER_MS = "300"` in `TestTimingEnv`. With
`WaitForDashboardPidAsync` timeout of ~5–10s, total test time stays under 20s.

### 5.2 Stale Test Fixes (Merged Single-Binary Architecture)

The 4 pre-existing tests assert old split-dashboard assumptions. Fix them to match
the current merged architecture:

| Test | Current assertion | Fixed assertion |
|---|---|---|
| `First_runtime_starts...` | `Assert.NotEqual(dashboardPid, runtime.Id)` | `Assert.Equal(dashboardPid, runtime.Id)` — backend IS the dashboard |
| `Second_runtime_reuses...` | Asserts temp project roots appear in runtimes | Align with `Alive()` filtering: only project-scoped runtimes (non-UserScope, non-root) are returned by `/api/coordinator/runtimes` |
| `Disconnect_removes_only...` | `Assert.Single(remaining)` on runtimes list | Same registry filtering alignment |
| `No_dashboard_flag...` | `--no-dashboard` flag asserted | Remove entirely — merged `Program.cs` has no flag parsing; port-blocked behavior is already covered by `Mcp_continues_when_dashboard_port_is_blocked` |

**Reason these failed before my change**: My test-infrastructure fix (HostDll + RepoRoot)
enabled these stale tests to actually *run* against the real binary. They were always broken
but never surfaced because the backend binary couldn't be launched.

### 5.3 Execution

Run per-csproj: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj`
with `ELING_TEST_TAKEOVER_MS=300` in the test process environment.

## 6. Files Affected

| File | Change |
|---|---|
| `src/backend/Eling.Backend/Bootstrap/McpHostArchitecture.cs` | Add `PortMonitor` static class; refactor peer path in `HttpLoop.RunAsync` |
| `src/backend/Eling.Backend/Bootstrap/DashboardPort.cs` | Add `ResolveTakeoverMs()` (parse `ELING_TAKEOVER_MS`, default 3000) |
| `tests/Eling.Backend.Tests/DashboardLifecycleTests.cs` | Flip verification test + fix 4 stale assertions |
| `tests/Eling.Backend.Tests/TestProcesses.cs` | Add `ELING_TEST_TAKEOVER_MS` to `TestTimingEnv` |
| `tests/Eling.Backend.Tests/McpProcessTests.cs` | Remove `No_dashboard_flag_skips_dashboard_startup` test |
| `docs/superpowers/specs/2026-09-02-port-takeover-design.md` | This document |

## 7. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Polling overhead during long owner lifetime | Low (single IsLoopbackListening call) | Negligible (port check is a TCP listener probe) | Configurable interval; default 3s is low-overhead |
| Multi-peer broadcast storm on promotion | Low (2 peers max in practice) | None — second to bind fails, loops back to peer | Existing `AddressAlreadyInUse` catch handles this |
| Frontend respawn race (two processes see 4427 free simultaneously) | Low | Brief 4427 downtime | `FrontendDevSpawner.TrySpawnPnpmFrontend` pre-kills listener; second process' attempt is a harmless no-op |
| Dev server side-effect during tests | Medium if 4427 is free in CI | Stray `pnpm dev:frontend` process | Guard `IsDevMode()` with an env flag for tests, or confirm 4427 is occupied; defer for now — test infrastructure already accepts this as-is |

## 8. Success Criteria

1. `Peer_takes_over_port_when_owner_exits` test PASSES (peer promotes within seconds).
2. All 4 stale lifecycle tests pass (merged-architecture assertions).
3. Existing owner-path tests still pass (`First_runtime...` now passes with `Equal`).
4. Manual verification: start two backends, close the first, confirm the second serves HTTP.
5. No new test failures across `Eling.Backend.Tests`.
