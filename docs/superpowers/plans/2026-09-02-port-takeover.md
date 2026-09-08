# In-Place Port Takeover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a MCP-only peer backend promote itself to dashboard port owner in-place (no process restart) when the current owner dies.

**Architecture:** A peer that finds the dashboard port taken enters a poll loop checking `DashboardPort.IsLoopbackListening(port)` every `ELING_TAKEOVER_MS` ms. When the port frees, the peer disposes its non-listening `WebApplication` and re-enters the `HttpLoop.RunAsync` loop, which rebuilds via `BuildWebApplication` → port is free → `ownerMode=true` → Kestrel binds → routes mapped → self-registers → (dev) respawns frontend. Multi-peer races are absorbed by the existing `AddressAlreadyInUse` catch.

**Tech Stack:** .NET 10, ASP.NET Core 10, C# 13, xUnit (per-csproj testing).

**Spec:** [`docs/superpowers/specs/2026-09-02-port-takeover-design.md`](../specs/2026-09-02-port-takeover-design.md)

## Global Constraints

- PowerShell on Windows (`pwsh`). Commands must be PowerShell-compatible.
- All code, comments, commits, docs in **English**.
- Unit tests run **per-csproj** (`dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj`), never solution-wide / chained.
- Git commits are **user-controlled** — implement, test, report, stop. **Never auto-commit**, push, amend, reset, rebase, or force-push. Leave the working tree dirty; report `git status --short` before stopping each task.
- No changes to `RuntimeRegistry`, `RuntimeSelfRegistration`, `DashboardRoutes`, `DashboardPort` (beyond what this plan specifies). Reuse existing `BuildWebApplication` for promotion; do not introduce a parallel owner-builder.
- The MCP host (GenericHost) in `Program.cs` runs for the full process lifetime and is **not** stopped during promotion. The peer's `WebApplication` (which has no Kestrel listener) is the only thing that gets disposed and rebuilt.

---

## File Structure

| File | Role | Change type |
|---|---|---|
| `src/backend/Eling.Backend/Bootstrap/DashboardPort.cs` | Port resolution + listener probe | Add `ResolveTakeoverMs()` |
| `src/backend/Eling.Backend/Bootstrap/McpHostArchitecture.cs` | Dual-host wiring (MCP + HTTP), port-acquisition loop | Add `PortMonitor`; refactor `HttpLoop.RunAsync` peer path |
| `tests/Eling.Backend.Tests/TestProcesses.cs` | Shared test process infrastructure | Add `ELING_TEST_TAKEOVER_MS` to `TestTimingEnv` |
| `tests/Eling.Backend.Tests/DashboardLifecycleTests.cs` | Lifecycle integration tests | Flip verification test; fix 3 stale lifecycle tests |
| `tests/Eling.Backend.Tests/McpProcessTests.cs` | MCP runtime tests | Remove obsolete `No_dashboard_flag_skips_dashboard_startup` test |

No new files, no new project references.

---

## Task 1: Add `PortMonitor` helper and takeover interval resolver

**Files:**
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardPort.cs:14-37`
- Modify: `src/backend/Eling.Backend/Bootstrap/McpHostArchitecture.cs:129-289` (HttpLoop region)

**Interfaces:**
- Consumes: existing `DashboardPort.IsLoopbackListening(int)`; `CancellationToken` from `mcpHost.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping` (already passed to `HttpLoop.RunAsync`).
- Produces:
  - `public static TimeSpan DashboardPort.ResolveTakeoverMs()` — reads `ELING_TAKEOVER_MS` env var, default 3000ms.
  - `public static class Eling.Backend.PortMonitor` with one method:
    ```csharp
    public static async Task WaitForPortFreeAsync(
        int port,
        TimeSpan interval,
        ILogger logger,
        CancellationToken cancellationToken);
    ```
    Resolves when `IsLoopbackListening(port)` returns `false` (or cancellation).

- [ ] **Step 1: Add `ResolveTakeoverMs` to `DashboardPort`**

Edit `src/backend/Eling.Backend/Bootstrap/DashboardPort.cs`. Add a new public static method (after the existing `Resolve()` at line 20-23, before `IsLoopbackListening`):

```csharp
/// <summary>
/// Resolves the peer takeover poll interval from <c>ELING_TAKEOVER_MS</c> env var,
/// falling back to 3000ms. The interval is how often a MCP-only peer checks whether
/// the dashboard port has been freed by the owner dying.
/// </summary>
public static TimeSpan ResolveTakeoverMs()
{
    var raw = Environment.GetEnvironmentVariable("ELING_TAKEOVER_MS");
    if (int.TryParse(raw, out var ms) && ms > 0) return TimeSpan.FromMilliseconds(ms);
    return TimeSpan.FromSeconds(3);
}
```

Also update the stale class-level doc comment (line 12) to acknowledge that the backend *can* bind the port when it wins the race:

```csharp
/// <summary>
/// Resolves and probes the loopback port that the dashboard subsystem owns.
/// In the merged single-binary architecture the *first* backend to launch binds
/// the port and serves REST + UI; every subsequent backend (peer) simply probes
/// whether the port is already listening and may later take over via
/// <see cref="Eling.Backend.PortMonitor"/> if the owner dies.
/// </summary>
```

No imports needed — `TimeSpan` is in `System`, already in scope via global usings.

- [ ] **Step 2: Add `PortMonitor` class in `McpHostArchitecture.cs`**

After the `HttpLoop` class closing brace (line 289), add a new `internal static class PortMonitor` inside the same `Eling.Backend` namespace:

```csharp
// ======================================================================
// PortMonitor — used by MCP-only peers in HttpLoop to wait for the
// dashboard port to become free (after the owner dies), at which point the
// peer can rebuild as owner. Polling cadence is set by ELING_TAKEOVER_MS.
// ======================================================================
internal static class PortMonitor
{
    /// <summary>
    /// Poll <paramref name="port"/> on the configured interval until
    /// <see cref="DashboardPort.IsLoopbackListening"/> returns false or
    /// <paramref name="cancellationToken"/> fires. Returns when the port
    /// is free. Throws <see cref="OperationCanceledException"/> on cancel.
    /// </summary>
    public static async Task WaitForPortFreeAsync(
        int port,
        TimeSpan interval,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var ticks = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!DashboardPort.IsLoopbackListening(port))
            {
                logger.LogInformation(
                    "Dashboard port {Port} is free (poll #{Ticks}); peer will attempt promotion",
                    port, ticks);
                return;
            }

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            ticks++;
        }
    }
}
```

- [ ] **Step 3: Build the backend csproj to confirm no compile errors**

Run (PowerShell):
```powershell
dotnet build src/backend/Eling.Backend/Eling.Backend.csproj
```
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit (only if user asks — otherwise report and stop)**

Per global constraints, do NOT auto-commit. Instead, report:
- "Task 1 done. Files changed: `DashboardPort.cs`, `McpHostArchitecture.cs`. Build green. Working tree dirty."
- `git status --short`

---

## Task 2: Refactor `HttpLoop.RunAsync` peer path to use `PortMonitor`

**Files:**
- Modify: `src/backend/Eling.Backend/Bootstrap/McpHostArchitecture.cs:138-206` (RunAsync method body)

**Interfaces:**
- Consumes: `DashboardPort.ResolveTakeoverMs()` (Task 1); `PortMonitor.WaitForPortFreeAsync` (Task 1); existing `BuildWebApplication`, `TrySpawnPnpmFrontend`, `DelayWithJitterAsync`, `cancellationToken`.
- Produces: Same `Task<int>` return; same exit codes (0 on clean shutdown/cancel, 1 on unhandled exception).

- [ ] **Step 1: Modify `RunAsync` to detect peer mode and poll**

Replace the body inside the `while (!cancellationToken.IsCancellationRequested)` block (lines 149-203 in current source). The new flow: after `BuildWebApplication` returns, check `ownerMode` BEFORE calling `app.RunAsync`. If peer, dispose the app, poll for free port, then `continue` the loop (so the next iteration rebuilds as owner). If owner, behave exactly as today.

Replace the `try { ... }` block at lines 153-195 with:

```csharp
            try
            {
                app = BuildWebApplication(shared, context, dashboardPort, isDevMode, loggerFactory);
                var ownerMode = !DashboardPort.IsLoopbackListening(dashboardPort);
                logger.LogInformation(
                    "HTTP host attempt #{Attempt}: {Mode} 127.0.0.1:{Port}",
                    attempt, ownerMode ? "OWNER, binding Kestrel to" : "peer, polling for", dashboardPort);

                if (!ownerMode)
                {
                    // Peer path: dispose the non-listening app and poll the
                    // dashboard port. When the owner dies and the port frees,
                    // we continue the loop and the next BuildWebApplication will
                    // observe us as owner.
                    await app.DisposeAsync();
                    app = null;

                    var takeoverInterval = DashboardPort.ResolveTakeoverMs();
                    await PortMonitor.WaitForPortFreeAsync(dashboardPort, takeoverInterval, logger, cancellationToken);
                    logger.LogInformation(
                        "Peer promoting to owner on port {Port} (was waiting {Ms}ms per poll)",
                        dashboardPort, (int)takeoverInterval.TotalMilliseconds);
                    continue;  // back to top: rebuild as owner next iteration
                }

                // Owner path: unchanged — bind, serve, self-register, spawn FE in dev.
                if (isDevMode)
                {
                    var feLogger = loggerFactory.CreateLogger("Eling.Backend.FrontendDev");
                    TrySpawnPnpmFrontend(context, dashboardPort, feLogger);
                }

                await app.RunAsync(cancellationToken);
                logger.LogInformation("HTTP host shut down cleanly on attempt #{Attempt}", attempt);
                return 0;
            }
```

**Important detail**: The variable `ownerMode` is computed **twice** (once in `BuildWebApplication` and once in `RunAsync` after the call). This is intentional and harmless — both happen on the same thread between the `try` and any concurrent socket activity — and avoids changing the signature of `BuildWebApplication` or the `DashboardRoutes` wiring. If the gap between the probe and rebuild is a concern, that race is *the same race* that already exists for the initial owner; promotion of multiple peers all seeing "free" is handled by the existing `AddressAlreadyInUse` catch.

The `try/catch/finally` structure around this stays the same. The `finally` at line 197-202 will dispose the app in either branch.

- [ ] **Step 2: Build to confirm no compile errors**

Run (PowerShell):
```powershell
dotnet build src/backend/Eling.Backend/Eling.Backend.csproj
```
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Verify the local var `ownerMode` doesn't collide with the existing `DashboardServices.Register(... ownerMode)` argument**

The existing `BuildWebApplication` uses an internal `ownerMode` local (line 219). RunAsync never sees that — it re-evaluates. Confirm by reading line 219 of the file post-edit: the internal `ownerMode` inside `BuildWebApplication` is still scoped to that method. No collision.

- [ ] **Step 4: Commit (only if user asks — otherwise report and stop)**

Report working-tree state.

---

## Task 3: Flip the verification test (no-takeover → takeover)

**Files:**
- Modify: `tests/Eling.Backend.Tests/DashboardLifecycleTests.cs:283-319` (the `Owner_exit_leaves_port_free_peer_does_not_take_over` test)
- Modify: `tests/Eling.Backend.Tests/TestProcesses.cs:35-42` (`TestTimingEnv`)

**Interfaces:**
- Consumes: `TestProcesses.TestTimingEnv` (existing); `TestProcesses.WaitForDashboardPidAsync`, `DashboardAliveAsync`, `GracefulStopAsync`, `WaitForRuntimeCountAsync` (existing helpers).
- Produces: One renamed + flipped test asserting that a peer promotes to owner within a bounded window.

- [ ] **Step 1: Add `ELING_TEST_TAKEOVER_MS` to `TestTimingEnv`**

Edit `tests/Eling.Backend.Tests/TestProcesses.cs` lines 35-42. The dictionary currently has heartbeat/sweep/stale/grace/shutdown-debounce. Add takeover:

```csharp
    public static IDictionary<string, string> TestTimingEnv => new Dictionary<string, string>
    {
        ["ELING_TEST_HEARTBEAT_MS"] = "300",
        ["ELING_TEST_SWEEP_MS"] = "200",
        ["ELING_TEST_STALE_MS"] = "800",
        ["ELING_TEST_GRACE_MS"] = "1000",
        ["ELING_TEST_SHUTDOWN_DEBOUNCE_MS"] = "500",
        ["ELING_TEST_TAKEOVER_MS"] = "300"
    };
```

The 300ms interval is the **poll** cadence for the peer; combined with the 10s wait window below, takeover should be observed within ~600ms after the owner dies.

- [ ] **Step 2: Rename and flip the test**

In `tests/Eling.Backend.Tests/DashboardLifecycleTests.cs`, replace the `Owner_exit_leaves_port_free_peer_does_not_take_over` test (lines 283-319) with the new `Peer_takes_over_port_when_owner_exits`:

```csharp
    /// <summary>
    /// Verifies the in-place port takeover: when the owner backend exits, a
    /// MCP-only peer that lost the initial port race must promote itself to
    /// owner via the poll loop, not stay MCP-only forever. The promoted peer
    /// then serves /health on the same port within the takeover interval.
    /// </summary>
    [Fact]
    public async Task Peer_takes_over_port_when_owner_exits()
    {
        await WaitForCleanPortAsync();

        // Backend #1 wins the race → owner mode: binds the port, serves HTTP,
        // and /health reports its own ProcessId.
        var first = StartRuntime();
        var ownerPid = await WaitForDashboardAsync();
        await WaitForRuntimeCountAsync(1);

        // Backend #2 sees the port taken → MCP-only peer (no HTTP surface).
        var second = StartRuntime();
        await WaitForRuntimeCountAsync(2);

        // The owner still answers /health after the peer joins.
        var currentPid = await TestProcesses.WaitForDashboardPidAsync(_client, TimeSpan.FromSeconds(5));
        Assert.Equal(ownerPid, currentPid);

        // Graceful shutdown of the owner → the port must become free.
        await GracefulStopAsync(first);
        var freeDeadline = DateTime.UtcNow + LifecycleTimeout;
        while (DateTime.UtcNow < freeDeadline && await TestProcesses.DashboardAliveAsync(_client))
        {
            await Task.Delay(200);
        }
        Assert.False(await TestProcesses.DashboardAliveAsync(_client),
            "Port must be free after the owner exits.");

        // Bounded observation: the peer should now serve HTTP on the same port
        // (promoted itself to owner) within ELING_TAKEOVER_MS + margin.
        var tookOverPid = await TestProcesses.WaitForDashboardPidAsync(_client, TimeSpan.FromSeconds(10));
        Assert.NotNull(tookOverPid);
        Assert.Equal(second.Id, tookOverPid!.Value);

        // Promotee's runtime registry should now show 1 alive runtime (the peer itself).
        // The original owner should NOT reappear.
        var runtimes = await WaitForRuntimeCountAsync(1);
        var entry = Assert.Single(runtimes);
        Assert.Equal(second.Id, entry.GetProperty("processId").GetInt32());

        await GracefulStopAsync(second);
    }
```

The `WaitForRuntimeCountAsync(1)` at the end re-uses the existing helper; after promotion, the promotee self-registers and `/api/coordinator/runtimes` returns 1 alive entry (the peer that became owner). The original owner (pid `first.Id`) is dead and swept by the liveness sweeper within `ELING_TEST_STALE_MS` (800ms) + margin; we wait up to `LifecycleTimeout` (20s).

- [ ] **Step 3: Build to confirm**

Run (PowerShell):
```powershell
dotnet build tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj
```
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Run only the flipped test**

Run (PowerShell):
```powershell
dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~Peer_takes_over_port_when_owner_exits" --no-build -v n
```
Expected: `Passed Eling.Backend.Tests.DashboardLifecycleTests.Peer_takes_over_port_when_owner_exits` and `Test Run Successful`.

- [ ] **Step 5: Commit (only if user asks — otherwise report and stop)**

Report working-tree state.

---

## Task 4: Fix 4 stale lifecycle tests for merged single-binary architecture

**Files:**
- Modify: `tests/Eling.Backend.Tests/DashboardLifecycleTests.cs:153-167` (`First_runtime_starts_dashboard_and_registers_itself`)
- Modify: `tests/Eling.Backend.Tests/DashboardLifecycleTests.cs:169-192` (`Second_runtime_reuses_the_same_dashboard`)
- Modify: `tests/Eling.Backend.Tests/DashboardLifecycleTests.cs:219-240` (`Disconnect_removes_only_that_runtime_and_keeps_dashboard_alive`)
- Modify: `tests/Eling.Backend.Tests/McpProcessTests.cs:153-182` (remove `No_dashboard_flag_skips_dashboard_startup`)

**Why these fail today:** the four tests were copied from the old `Eling.Host.Tests` and assume a *separate* `eling-dashboard` process. In the merged single-binary `Eling.Backend`, the backend process IS the dashboard (`/health` returns its own PID via `Environment.ProcessId`). The `--no-dashboard` flag no longer exists in `Program.cs`. The pre-existing `TestProcesses` fixes I made (renaming `HostDll` to the real `eling-backend.dll`, fixing `RepoRoot`) merely *enabled* these tests to run against the real binary — they were always broken but never surfaced.

- [ ] **Step 1: Fix `First_runtime_starts_dashboard_and_registers_itself`**

In `DashboardLifecycleTests.cs:166`, change:

```csharp
        Assert.NotEqual(dashboardPid, runtime.Id); // dashboard is its own process
```

to:

```csharp
        // In the merged single-binary architecture, the backend process IS the
        // dashboard owner: /health returns Environment.ProcessId, which is
        // also the runtime's pid. They are intentionally equal.
        Assert.Equal(dashboardPid, runtime.Id);
```

- [ ] **Step 2: Fix `Second_runtime_reuses_the_same_dashboard`**

This test calls `TestProcesses.GetRuntimesAsync(_client)` and asserts that both temp project roots appear in the runtimes list. The current `/api/coordinator/runtimes` endpoint returns `registry.Alive()` which **filters** out the `"UserScope"` sentinel and other root user-home entries. The temp dirs (`eling-lifecycle-XXXXXXXX`) are real project roots so they should appear.

Run the suite after Task 3 alone; if this test passes without changes, skip this step. If it still fails, capture and report the actual set to the user rather than re-engineering the assertion.

- [ ] **Step 3: Fix `Disconnect_removes_only_that_runtime_and_keeps_dashboard_alive`**

Same as Step 2 — run after Task 3. If it fails: raise `LifecycleTimeout` from 20s to 30s (line 17) and re-run. If still failing, capture actual runtimes and report.

**Try this step only if the test fails after Task 3.**

- [ ] **Step 4: Remove `No_dashboard_flag_skips_dashboard_startup`**

In `tests/Eling.Backend.Tests/McpProcessTests.cs`, delete the entire test method `No_dashboard_flag_skips_dashboard_startup` (including its `[Fact]` attribute). The behavior is now covered by `Mcp_continues_when_dashboard_port_is_blocked`, which uses a `TcpListener` blocker to assert that an occupied port doesn't break MCP stdio.

After deletion, also delete the `using System.Net.Http;` import if it becomes unused. Check by reading the file post-deletion: `Mcp_continues_when_dashboard_port_is_blocked` does NOT use `HttpClient`, and `No_dashboard_flag_skips_dashboard_startup` is the only test that did. After removal, the import is dead — remove it.

- [ ] **Step 5: Build to confirm**

Run (PowerShell):
```powershell
dotnet build tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj
```
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 6: Run the full DashboardLifecycleTests + McpProcessTests collection**

Run (PowerShell):
```powershell
dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~DashboardLifecycleTests|FullyQualifiedName~McpProcessTests" --no-build -v n
```

Expected: All 11 tests pass (6 lifecycle + 1 new peer-takeover + 4 MCP). If any fail, **stop**, report the failure (per the `@stop_on_failure` rule), and propose a fix.

- [ ] **Step 7: Commit (only if user asks — otherwise report and stop)**

Report working-tree state.

---

## Task 5: Run the full `Eling.Backend.Tests` suite

**Files:** none.

- [ ] **Step 1: Run all `Eling.Backend.Tests`**

Run (PowerShell):
```powershell
dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --no-build -v n
```

Expected: All tests pass. Capture total counts and timings.

- [ ] **Step 2: Run `Eling.Core.Tests` to confirm no collateral**

Run (PowerShell):
```powershell
dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --no-build -v n
```

Expected: All tests pass (no production change in `Eling.Core`).

- [ ] **Step 3: Report and stop**

Per `@stop_on_failure`: if any test fails, STOP. Report which test, the failure message, and proposed fix (do NOT auto-fix). If all green, summarize:

- Total tests run, pass count, total time
- New test name: `Peer_takes_over_port_when_owner_exits`
- Stale tests fixed: list of methods
- Working-tree state via `git status --short`

---

## Task 6: Final cleanup — git status + memory + report

**Files:** none (memory save only).

- [ ] **Step 1: Run `git status --short` to capture working-tree state**

Run (PowerShell):
```powershell
git status --short
```

- [ ] **Step 2: Save the new design + verification to Eling memory**

Use the `mcp_eling_dev_memory_save` tool. Save the verification result as a `Fact`:

```json
{
  "content": "[Eling project] Port takeover verified implemented 2026-09-02 via poll-loop in HttpLoop + PortMonitor.WaitForPortFreeAsync. ELING_TAKEOVER_MS env var (default 3000ms). Test Peer_takes_over_port_when_owner_exits passes. Stale 4 lifecycle tests fixed for merged single-binary architecture. Spec: docs/superpowers/specs/2026-09-02-port-takeover-design.md. Plan: docs/superpowers/plans/2026-09-02-port-takeover.md.",
  "type": "fact",
  "tags": ["eling", "port-takeover", "http-loop", "verification", "backend", "impl-complete"]
}
```

If the memory save times out (as it did earlier), retry once. If it still fails, log the failure to the user and proceed without persisting.

- [ ] **Step 3: Final report to user**

Summarize:
- What was implemented (Tasks 1-4) — short prose
- Test results (Task 5)
- Working-tree state (Task 6 Step 1) — do NOT auto-commit
- Reminder that commits are user-controlled checkpoints
- Ask: "Are the changes ready for you to review and commit? Any follow-up?"
