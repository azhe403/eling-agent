# Realtime Workspace Metrics SSE Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Push per-process CPU/RAM samples from the dashboard owner to the workspaces page every 2 seconds over a dedicated SSE stream, replacing the page's 10-second poll.

**Architecture:** A stateless `RuntimeMetricsSampler` turns live pids into `RuntimeMetricsSnapshot` batches; a typed `RuntimeMetricsBroadcaster` fans batches out to subscribers and runs a 2s `PeriodicTimer` pump that lives only while at least one subscriber exists. The frontend merges frames into a `metricsByPid` overlay and re-runs the existing pure `group()` aggregation.

**Tech Stack:** ASP.NET Core minimal APIs (.NET 10, `System.Threading.Channels`, SSE), Next.js 16 / React 19 client hooks, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-15-realtime-workspace-metrics-sse-design.md`

## Global Constraints

- Tick is 2 seconds; the broadcaster takes an optional `TimeSpan? interval` (default `TimeSpan.FromSeconds(2)`) so tests can inject a short one.
- The SSE route is owner-mode only (registered inside the `isOwnerMode` block of `DashboardServices`; mapped in `MapSseEvents` of `DashboardRoutes`, reusing the private `WriteSseAsync` and `SseJsonOptions`).
- Wire format: one frame = JSON array of `{"pid","memoryBytes","cpuPercent","sampledAt"}`, camelCase, via `JsonSerializer.Serialize(batch, SseJsonOptions)`.
- `GET /api/coordinator/runtimes` stays untouched — no shared cache between the pump and the on-demand endpoint.
- The workspaces page's 10s metric poll is removed; a 30s safety poll over both existing endpoints remains for codebase statuses, heartbeats, and missed events.
- Frontend tasks: the repo's agent-rules block requires reading `node_modules/next/dist/docs/` (resolved from `src/frontend/Eling.Dashboard/`) before writing any frontend code.
- The pre-commit hook runs the full test suite — long runs are expected; no artificial delay between commits.

## Review Focus

- **Metrics stream dies while the page stays open** (backend restart / port takeover): numbers would freeze silently. The header pill must go amber/red and a reconnect must resync — pinned in Task 6 (pill merge + `onOpen` resync), verified live in Task 7.
- **Pump-gate race** (last subscriber leaves and rejoins between ticks): stream looks connected but never publishes again — pinned in Task 2 (subscribe-after-stop restarts the pump).
- **A pid dies between registry read and probe**: the frame must omit that pid, not throw or drop the whole batch — pinned in Task 1.
- **Two tabs subscribed at once**: both receive every frame; one tab closing must not disturb the other — pinned in Task 2.
- **First frames after a pid starts** show CPU `0.0` until a second sample ≥0.2s exists (tracker delta rule): must stay a transient value, never an error — pinned in Task 1 (second `SampleAll()` yields a valid `[0,100]` CPU).

---

### Task 1: RuntimeMetricsSnapshot DTO + RuntimeMetricsSampler

**Files:**
- Create: `src/backend/Eling.Backend/Dtos/RuntimeMetricsDto.cs`
- Create: `src/backend/Eling.Backend/Services/RuntimeMetricsSampler.cs`
- Test: `tests/Eling.Backend.Tests/RuntimeMetricsSamplerTests.cs`

**Interfaces:**
- Consumes: `Eling.Backend.RuntimeRegistry` (`IReadOnlyList<RuntimeInfo> Alive(bool allProcesses = false)`), `Eling.Backend.Services.ProcessMetricsTracker` (`RuntimeProcessMetrics? Sample(int pid)`; ctor takes `ILogger<ProcessMetricsTracker>`).
- Produces: `public sealed record RuntimeMetricsSnapshot(int Pid, long MemoryBytes, double CpuPercent, DateTimeOffset SampledAt)` in `Eling.Backend.Dtos`; `RuntimeMetricsSampler` in `Eling.Backend.Services` with `public IReadOnlyList<RuntimeMetricsSnapshot> SampleAll()`.

- [ ] **Step 1: Write the failing test** — class `RuntimeMetricsSamplerTests`, registry built like `CoordinatorControllerTests` (temp dir, `UserScope.Resolve`, `JsonWorkspaceRegistry`, `NullLogger<RuntimeRegistry>.Instance`), tracker `new ProcessMetricsTracker(NullLogger<ProcessMetricsTracker>.Instance)`. Tests: (a) registering `Environment.ProcessId` → `SampleAll()` contains a snapshot for it with `MemoryBytes > 0`; (b) registering a dead pid (e.g. `int.MaxValue`) alongside the live one → live pid present, dead pid omitted, no throw; (c) calling `SampleAll()` twice a moment apart keeps the live pid's `CpuPercent` within `[0, 100]`; (d) empty registry → empty list.
- [ ] **Step 2: Run to verify red** — `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~RuntimeMetricsSamplerTests" --artifacts-path .bin-test` → fails (types do not exist yet).
- [ ] **Step 3: Implement the DTO and sampler** — record in `Dtos/RuntimeMetricsDto.cs`; sampler ctor `(RuntimeRegistry registry, ProcessMetricsTracker metricsTracker, ILogger<RuntimeMetricsSampler>? logger = null)`; `SampleAll()` reads `registry.Alive(allProcesses: true)`, skips rows where `!IsAlive || ProcessId <= 0`, calls `metricsTracker.Sample(pid)`, maps non-null results to snapshots stamping `SampledAt = DateTimeOffset.UtcNow`. Per-pid failures already degrade inside the tracker; do not add extra try/catch.
- [ ] **Step 4: Run to verify green** — same filter, all four tests pass.
- [ ] **Step 5: Commit** — `git add src/backend/Eling.Backend/Dtos/RuntimeMetricsDto.cs src/backend/Eling.Backend/Services/RuntimeMetricsSampler.cs tests/Eling.Backend.Tests/RuntimeMetricsSamplerTests.cs && git commit -m "feat(backend): sample live runtime metrics for SSE streaming"`

---

### Task 2: RuntimeMetricsBroadcaster with subscriber-gated pump

**Files:**
- Create: `src/backend/Eling.Backend/Services/RuntimeMetricsBroadcaster.cs`
- Test: `tests/Eling.Backend.Tests/RuntimeMetricsBroadcasterTests.cs`

**Interfaces:**
- Consumes: `RuntimeMetricsSampler.SampleAll()` (Task 1), `IReadOnlyList<RuntimeMetricsSnapshot>`.
- Produces:
  - `public void Publish(IReadOnlyList<RuntimeMetricsSnapshot> batch)`
  - `public async IAsyncEnumerable<IReadOnlyList<RuntimeMetricsSnapshot>> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)`
  - ctor `(RuntimeMetricsSampler sampler, ILogger<RuntimeMetricsBroadcaster> logger, Func<IReadOnlyList<RuntimeMetricsSnapshot>>? sampleFactory = null, TimeSpan? interval = null)`; `sampleFactory` defaults to `sampler.SampleAll`, `interval` to 2s. The factory is the seam the gate tests count invocations through.

- [ ] **Step 1: Write the failing test** — class `RuntimeMetricsBroadcasterTests`. Tests: (a) two open `SubscribeAsync` enumerators both receive the identical batch from one `Publish`; (b) `Publish` with no subscribers does not throw; (c) with `interval = TimeSpan.FromMilliseconds(50)` and a counting factory, one subscriber receives at least two batches, and after the enumerator is disposed the count stops increasing across a 200ms wait (pump stopped); (d) subscribing again, publishing once, and receiving it proves the pump restarts (gate).
- [ ] **Step 2: Run to verify red** — `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~RuntimeMetricsBroadcasterTests" --artifacts-path .bin-test`.
- [ ] **Step 3: Implement** — unbounded `Channel<IReadOnlyList<RuntimeMetricsSnapshot>>` per subscriber under a lock, same as `CodebaseRebuildBroadcaster`. Subscribe adds the channel, and when the count went 0→1 creates a fresh `CancellationTokenSource` and starts `Task.Run(PumpAsync)`. `PumpAsync`: `using var timer = new PeriodicTimer(interval)`; publish one immediate batch, then `while (await timer.WaitForNextTickAsync(cts.Token))` publish `sampleFactory()`, wrapped per-tick in try/catch that logs at debug and continues. Unsubscribe (the async iterator's `finally`) removes the channel and, when the count hits 0, cancels the pump CTS. Start/stop decisions only under the same lock.
- [ ] **Step 4: Run to verify green** — same filter, all four tests pass.
- [ ] **Step 5: Commit** — `git add src/backend/Eling.Backend/Services/RuntimeMetricsBroadcaster.cs tests/Eling.Backend.Tests/RuntimeMetricsBroadcasterTests.cs && git commit -m "feat(backend): fan out runtime metrics over a gated SSE channel"`

---

### Task 3: SSE route + DI registration + route integration test

**Files:**
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` — inside the `if (isOwnerMode)` block, register via explicit factory:
  `services.AddSingleton(sp => new RuntimeMetricsBroadcaster(sp.GetRequiredService<RuntimeMetricsSampler>(), sp.GetRequiredService<ILogger<RuntimeMetricsBroadcaster>>()));` plus `services.AddSingleton<RuntimeMetricsSampler>();` (tracker already registered earlier in the file).
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` — in `MapSseEvents`, after the codebase-rebuild route:
  `app.MapGet("/api/events/runtime-metrics", (HttpContext context, RuntimeMetricsBroadcaster broadcaster) => { var ct = context.RequestAborted; return WriteSseAsync(context, broadcaster.SubscribeAsync(ct), item => JsonSerializer.Serialize(item, SseJsonOptions), ct); });`
- Test: `tests/Eling.Backend.Tests/MemoryApiTests.cs` — add test method only (fixture already registers `Environment.ProcessId` as a runtime).

**Interfaces:**
- Consumes: `RuntimeMetricsBroadcaster.SubscribeAsync` (Task 2).
- Produces: `GET /api/events/runtime-metrics` — `data: connected`, then snapshot-array frames within ~2s.

- [ ] **Step 1: Write the failing test** — `[Fact] GetMetricsStream_Subscribed_ReceivesSnapshotFrames`: send with `HttpCompletionOption.ResponseHeadersRead` under a 10s CTS; assert first line is `data: connected` and content type is `text/event-stream`; read lines until one starts with `data: [`; deserialize the JSON array; assert length ≥ 1 and the entry for `Environment.ProcessId` has `memoryBytes > 0`.
- [ ] **Step 2: Run to verify red** — `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~GetMetricsStream_Subscribed_ReceivesSnapshotFrames" --artifacts-path .bin-test` → 404 / connection error.
- [ ] **Step 3: Wire DI and route** per the file list above (`RuntimeMetricsSampler` resolves `RuntimeRegistry` + `ProcessMetricsTracker` from existing registrations; `RuntimeRegistry` is registered before the owner block).
- [ ] **Step 4: Run to verify green** — same filter passes (allow ~4s for the first tick inside the 10s CTS).
- [ ] **Step 5: Commit** — `git add src/backend/Eling.Backend/Bootstrap/DashboardServices.cs src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs tests/Eling.Backend.Tests/MemoryApiTests.cs && git commit -m "feat(backend): serve runtime metrics over /api/events/runtime-metrics"`

---

### Task 4: Frontend metrics SSE hook

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/hooks/use-runtime-metrics-sse.ts`

**Interfaces:**
- Produces:
  - `export type RuntimeMetricsSnapshot = { pid: number; memoryBytes: number; cpuPercent: number; sampledAt: string }`
  - `export type RuntimeMetricsSseStatus = "connected" | "connecting" | "error"`
  - `export function useRuntimeMetricsSse(onMetrics: (samples: RuntimeMetricsSnapshot[]) => void, onOpen?: () => void): { status: RuntimeMetricsSseStatus }`

- [ ] **Step 1: Read the frontend agent rules' referenced docs** — from `src/frontend/Eling.Dashboard/`, open `node_modules/next/dist/docs/` and skim what the repo's rules block points to (client-component/hook guidance) before writing code.
- [ ] **Step 2: Write the hook** — mirror `use-codebase-rebuild-sse.ts` (callback stored in a ref, `EventSource("/api/events/runtime-metrics")`, close on cleanup, `onopen` → status `connected` and call `onOpen` once so a reconnect can resync, `onmessage` → `JSON.parse(event.data)` as `RuntimeMetricsSnapshot[]` with a `console.warn` on malformed frames like the rebuild hook, `onerror` → `connecting` while `EventSource.CONNECTING` else `error`).
- [ ] **Step 3: Typecheck** — `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit` → clean.
- [ ] **Step 4: Commit** — `git add src/frontend/Eling.Dashboard/src/hooks/use-runtime-metrics-sse.ts && git commit -m "feat(dashboard): subscribe to the runtime metrics SSE stream"`

---

### Task 5: aggregate.ts metrics overlay

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/workspaces/aggregate.ts`

**Interfaces:**
- Consumes: `RuntimeMetricsSnapshot` from the hook (Task 4).
- Produces: `export type ProcessMetricSample = { memoryBytes: number; cpuPercent: number }` and `group(runtimes: RuntimeDto[], metricsByPid?: ReadonlyMap<number, ProcessMetricSample>)` — same return type; per-process `memoryBytes`/`cpuPercent` take the overlay value when present, else the DTO's own value, else `null`; aggregation (sums, owner badge, sorting) unchanged.

- [ ] **Step 1: Add the type and the optional parameter**, applying the overlay in the per-row `WorkspaceProcess` mapping before the sums are computed, so one code path feeds both the tiles and the drill-down.
- [ ] **Step 2: Typecheck** — `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit` → clean.
- [ ] **Step 3: Commit** — `git add src/frontend/Eling.Dashboard/src/app/dashboard/workspaces/aggregate.ts && git commit -m "feat(dashboard): merge streamed metrics into the workspace aggregate"`

---

### Task 6: WorkspacesList wiring — drop the 10s poll, fold in the stream

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/workspaces/WorkspacesList.tsx`

**Interfaces:**
- Consumes: `useRuntimeMetricsSse` (Task 4), `ProcessMetricSample` + `group(runtimes, overlay)` (Task 5), existing `useMemoriesSse` (unchanged).

- [ ] **Step 1: Derive `groups` from state** — hold `runtimes: RuntimeDto[]` and `metricsByPid: ReadonlyMap<number, ProcessMetricSample>` in state; `loadData` sets `runtimes` (and `statusMap`, `loading`, error) as today; one `useEffect(() => setGroups(group(runtimes, metricsByPid)), [runtimes, metricsByPid])` replaces the direct `setGroups` calls. Dead pids lingering in the overlay are harmless — `group()` only maps live processes.
- [ ] **Step 2: Replace the poll block (lines ~140–150)** — delete the `POLL_MS = 10_000` interval; add a `SAFETY_POLL_MS = 30_000` interval calling `loadData` with a comment stating metrics now arrive via SSE and this poll exists only for codebase statuses, heartbeats, and any missed event.
- [ ] **Step 3: Wire the hook** — `useRuntimeMetricsSse(handleMetrics, handleReload)` where `handleMetrics` merges the frame into `metricsByPid` (copy-on-write `Map`, set by pid) and `handleReload` is the existing lifecycle reload, now also used as the reconnect resync (`onOpen`). Keep the existing `useMemoriesSse` subscription for lifecycle/codebase events.
- [ ] **Step 4: Make the pill honest** — compute a combined status from both streams (`connected` only when both are; `error` when either is; otherwise `connecting`) and use it in the header pill's color/label/title.
- [ ] **Step 5: Typecheck** — `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit` → clean.
- [ ] **Step 6: Commit** — `git add src/frontend/Eling.Dashboard/src/app/dashboard/workspaces/WorkspacesList.tsx && git commit -m "feat(dashboard): live CPU/RAM on workspaces page via metrics SSE"`

---

### Task 7: Full verification

**Files:** none — verification only.

- [ ] **Step 1: Backend suite** — `dotnet test Eling.slnx --artifacts-path .bin-test` → green (the pre-commit hook already runs this; run it once explicitly for the whole plan).
- [ ] **Step 2: Frontend build** — `pnpm --dir src/frontend/Eling.Dashboard build` → clean.
- [ ] **Step 3: Live check** — with both dev servers up (backend 4417, frontend 4427): open `/dashboard/workspaces` in the OpenChamber browser panel (first compile can take up to a minute — that is the known gotcha, not a hang); confirm CPU/RAM values move roughly every 2s, the multi-instance drill-down moves too, the pill is green; restart the backend and confirm the pill goes amber/red, then green again with fresh numbers after reconnect.
- [ ] **Step 4: Ask the user whether to clean up `.tmp`/session artifacts** if any were produced.
