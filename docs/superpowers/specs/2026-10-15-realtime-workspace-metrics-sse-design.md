# Realtime Workspace Metrics (CPU/RAM) via SSE — Design Specification

- **Date:** 2026-10-15
- **Status:** Proposed (awaiting user review)
- **Target Projects:** `Eling.Backend`, `Eling.Dashboard` (frontend), `tests/Eling.Backend.Tests`

---

## 1. Overview & Scope

The dashboard's workspaces page shows per-workspace CPU and RAM aggregated from every live
eling process (`GET /api/coordinator/runtimes?allProcesses=true`, grouped client-side in
`aggregate.ts`). Those numbers are volatile, but nothing pushes them: the page keeps them
current with a 10-second interval poll, so the values age for up to ten seconds. This spec
adds a server-sent events stream that samples live runtimes every 2 seconds and pushes the
batch to any open page, so the metrics move at the pace of the data instead of the pace of a
poll. Live process membership (register/unregister/sweep) and codebase index events already
travel over the existing topic-name SSE stream and remain untouched.

**In scope:**

- `RuntimeMetricsSampler` (backend, stateless): live pids → `RuntimeMetricsSnapshot` batch
- `RuntimeMetricsBroadcaster` (backend, typed pub/sub + 2s pump gated on subscriber count)
- `GET /api/events/runtime-metrics` SSE route (owner mode only), reusing `WriteSseAsync`
- `use-runtime-metrics-sse` hook (frontend) mirroring `use-codebase-rebuild-sse`
- Workspaces page: merge frames into a `metricsByPid` overlay, drop the 10s poll, keep a
  30s safety poll for codebase statuses, fold the metrics-stream status into the header pill
- Backend unit tests for sampler + broadcaster; one SSE route test (precedent:
  `MemoryApiTests.GetEventsStream_*`)

**Out of scope (deferred, flagged — see §8):** sparklines/CPU history, sub-2s cadence,
per-value pulse animations, changes to `GET /api/coordinator/runtimes`, metrics exposed via
MCP tools, bounded-channel backpressure.

---

## 2. Architecture & Components

Follows the repo split: probing/publishing in `Eling.Backend/Services` (next to
`ProcessMetricsTracker`), the wire DTO in `Eling.Backend/Dtos` (next to `RuntimeInfoDto`),
SSE wiring in the existing owner-mode bootstrap.

```
Eling.Backend/
  Dtos/RuntimeMetricsDto.cs               record RuntimeMetricsSnapshot(Pid, MemoryBytes, CpuPercent, SampledAt)
  Services/RuntimeMetricsSampler.cs       stateless: registry.Alive() → tracker.Sample(pid) → snapshots
  Services/RuntimeMetricsBroadcaster.cs   typed fan-out (pattern: CodebaseRebuildBroadcaster) + gated pump
  Bootstrap/DashboardServices.cs          DI: both singletons inside the isOwnerMode block (edit)
  Bootstrap/DashboardRoutes.cs            MapGet("/api/events/runtime-metrics") in MapSseEvents (edit)

Eling.Dashboard/src/
  hooks/use-runtime-metrics-sse.ts        EventSource for the metrics stream (pattern: use-codebase-rebuild-sse.ts)
  app/dashboard/workspaces/aggregate.ts   group(runtimes, metricsByPid?) overlay parameter (edit)
  app/dashboard/workspaces/WorkspacesList.tsx  overlay state, drop 10s poll, 30s safety poll, pill status (edit)

tests/Eling.Backend.Tests/
  RuntimeMetricsSamplerTests.cs           new
  RuntimeMetricsBroadcasterTests.cs       new
  MemoryApiTests.cs                       new test: GetMetricsStream_Subscribed_ReceivesSnapshotFrames
```

**Two streams, two jobs.** The topic-name stream (`/api/events/memories`) keeps carrying
lifecycle and index events; it is a change-notification contract shared by several pages, and
putting payloads on it would change that contract. The new channel carries only volatile
metric frames. A page therefore reacts to: lifecycle events (full reload), codebase events
(full reload), and metric frames (cheap overlay merge).

**Why a pump inside the broadcaster.** The dashboard owner process runs all day while the
workspaces page is frequently closed. Gating sampling on subscriber count keeps the cost
proportional to actual watching; the alternative — an always-on `BackgroundService` — would
probe every 2 seconds forever for nobody. The gate lives in the broadcaster because that is
the only place that knows subscriber count, and its start/stop is observable behavior
tests can assert (interval is a constructor parameter, defaulting to 2s).

---

## 3. Data Model & Wire Format

```csharp
/// One live process as sampled at one instant.
public sealed record RuntimeMetricsSnapshot(
    int Pid,
    long MemoryBytes,
    double CpuPercent,
    DateTimeOffset SampledAt);
```

One SSE frame is a JSON array of the snapshots of all live pids at that tick:

```
data: [{"pid":12844,"memoryBytes":214748364,"cpuPercent":3.2,"sampledAt":"2026-10-15T09:31:02.41Z"}]

```

- camelCase via the existing `SseJsonOptions`/`Serialize`-style helper on the route.
- A pid whose probe fails (process exited between registry read and probe) is omitted from
  the frame; `ProcessMetricsTracker` already degrades that way.
- Empty registry → `[]` frames. Frames carry no membership: a pid that disappears leaves the
  frame, and its row is removed by the lifecycle event that follows the real unregister/sweep.

---

## 4. Data Flow & Behavior

**Backend.** On `GET /api/events/runtime-metrics`, the route calls
`broadcaster.SubscribeAsync(context.RequestAborted)` through the shared `WriteSseAsync`
(initial `data: connected`, 15s comment ping, abort on disconnect). The first subscriber
starts a `PeriodicTimer(2s)` loop: sample all live pids via `RuntimeMetricsSampler`, then
`Publish(batch)` — one immediate first sample on subscribe so RAM appears at once, then once
per tick. `Publish` writes to every subscriber channel with `TryWrite` under the same lock
that guards the subscriber list. When the last subscriber's `finally` removes its channel,
the pump stops. Start/stop decisions happen under that same lock, so a subscribe racing a
stop restarts the pump instead of leaving a silent stream.

**Frontend.** `use-runtime-metrics-sse` opens the EventSource on mount, parses each frame as
`RuntimeMetricsSnapshot[]`, and hands it to a callback stashed in a ref (same pattern as the
rebuild hook). On `onopen` — including EventSource's automatic reconnects — the page performs
one full reload, because frames missed while disconnected would otherwise leave stale numbers
on screen. `WorkspacesList` keeps the raw `runtimes` list and a `metricsByPid` map in state;
each frame updates the map and re-runs `group(runtimes, metricsByPid)`, which now takes an
optional overlay: a process's `memoryBytes`/`cpuPercent` come from the overlay when present,
`null` stays `null` until a sample exists. Aggregation (sums, drill-down, owner badge) is
unchanged and still lives only in `aggregate.ts`.

**Polling changes.** The 10s interval is removed — it existed solely to keep CPU/RAM live, a
job the stream now does every 2s. The lifecycle/codebase SSE subscription stays as-is (full
reload on register/unregister/sweep/re-index), and a 30s safety poll fetches both existing
endpoints so codebase statuses, heartbeats, and the registry itself self-heal if an event is
ever missed. The header "Live" pill becomes the combined status of both streams: green only
when both are connected, amber while either reconnects, red when either reports closed — one
honest indicator instead of a pill that claims live while the numbers are frozen.

**Known cosmetic effect.** `CpuPercent` is a delta between two samples ≥0.2s apart. A pid's
first frame can show correct RAM with CPU `0.0`; the next frame (2s later) is correct.
Likewise, a manual Refresh within 0.2s of a tick may briefly read CPU `0` from the on-demand
endpoint — the tracker keeps its previous sample, so the pump's next value is unaffected.

---

## 5. Error Handling & Edge Cases

- **Probe failure** (process exits mid-tick): tracker returns null, pid omitted from the
  frame; the real removal arrives as a `runtimes` lifecycle event. Not an error.
- **Tick failure** (unexpected probe exception): the loop catches, logs via the injected
  `ILogger`, and continues — one dead pid or one bad probe must never kill the stream.
- **Aborted subscriber**: `SubscribeAsync`'s `finally` removes the channel and completes the
  writer; Kestrel tears down the response via `context.RequestAborted`.
- **Subscribe/stop race**: start and stop are decided under the lock that owns the subscriber
  list; a subscribe arriving after the count hit zero restarts the pump.
- **Stalled client**: channels stay unbounded with `TryWrite` (same as
  `CodebaseRebuildBroadcaster`). On loopback a hung tab can buffer a few frames; accepted
  for pattern consistency — switching to bounded `DropOldest` later is a one-class change (§8).
- **Metrics stream down, lifecycle stream up** (backend restart, port takeover): pill shows
  amber/red; the 30s safety poll still keeps the list correct but metrics go stale — the
  indicator exists precisely so this state is not invisible.
- **Owner vs peer**: only the owner maps SSE routes; the owner samples all live pids,
  including peers — identical to today's endpoint behavior.

---

## 6. Testing

Backend (xUnit, patterns from `ProcessMetricsTests` / `CoordinatorControllerTests`):

- `RuntimeMetricsSamplerTests`: registry with the test's own pid → batch contains it with
  `MemoryBytes > 0`; a dead pid is omitted; empty registry → empty batch.
- `RuntimeMetricsBroadcasterTests`: two subscribers both receive the same published batch;
  after the last unsubscribes, nothing is delivered; `Publish` with no subscribers does not
  throw; with a small injected interval the pump delivers more than one batch and stops when
  the subscriber leaves.
- `MemoryApiTests` addition: `GET /api/events/runtime-metrics` opens with
  `data: connected`, then at least one snapshot frame arrives within the test's 10s CTS (the
  fixture already registers the test process as a runtime).

Frontend has no test infrastructure (`package.json` ships only dev/build/start/lint), so the
merge logic is verified in the running app rather than by unit tests: the dev-server page is
opened in the OpenChamber browser panel and observed across several ticks (first compile may
take up to a minute — the page looks stuck while Next compiles on demand).

---

## 7. Verification

1. `dotnet test` green for `Eling.Backend.Tests` (and full solution, since the pre-commit
   hook runs it anyway).
2. Frontend `pnpm build` (typecheck) clean; Next docs read per the repo's agent-rules block
   before touching any frontend code.
3. Live check at `/dashboard/workspaces`: CPU/RAM values move every ~2s; expanding a
   multi-instance workspace shows per-process values moving too; restarting the backend
   turns the pill amber then green again after reconnect.

---

## 8. Deferred

- Sparklines / CPU history / sub-2s cadence — revisit only when the UI wants a chart; the
  payload already carries `sampledAt` to support it.
- Bounded-channel backpressure (`DropOldest`) — only if a stalled-client buffer is ever
  observed; localized to the broadcaster.
- Metrics via MCP tools, persisted history, or a `/api/coordinator/runtimes` change — none
  requested; the endpoint stays the on-demand snapshot it is today.
