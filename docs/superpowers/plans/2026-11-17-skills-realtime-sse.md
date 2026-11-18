# Realtime Skills Page via SSE Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **User's standing rules:** never commit a spec doc (the spec stays in the working tree for the user to commit); split commits per logical context; hooks are mandatory (no `--no-verify`); frontend is verified on the live dev server at 4427, never via `pnpm build` / `next build` while it runs; backend dev runs on 4417 under `dotnet watch` — do not kill it, build to `.bin-test` or a separate artifacts path.

**Goal:** The `/dashboard/skills` page refreshes automatically — list and open detail panel — whenever the machine-global skill catalog changes, whether the change came through eling (MCP tools / REST) or from external tooling writing the catalog folder directly.

**Architecture:** A new SSE topic `skills` on the existing shared `/api/events/memories` stream. Two complementary notification paths, both landing on the owner's `MemoryChangeBroadcaster`: (1) eling-mediated mutations — `ISkillChangeNotifier` (already called by `SkillService`) wired to an in-proc adapter on the owner and to a fire-and-forget HTTP relay in the MCP host, mirroring `HttpCoordinatorCommitLeaseChangeNotifier`; (2) on-disk changes by any process — a new `SkillCatalogDirWatcher` (owner-only `IDisposable`, mirrors `AuditDirWatcher`) watches `~/.agents/skills` recursively, sets a dirty flag per event, and on a 1000 ms `PeriodicTimer` tick runs a full `ISkillCatalogScanner.ScanAsync()` then broadcasts `skills`. The frontend subscribes via `useMemoriesSse(..., ["skills"])` and refetches list + detail silently.

**Tech Stack:** ASP.NET Core minimal APIs (.NET 10), `System.IO.FileSystemWatcher`, `PeriodicTimer`, Next.js 16 / React 19 client hook (`useMemoriesSse`), vitest + Testing Library, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-17-skills-realtime-sse-design.md` (read it first — it carries the decisions: approach C, silent reload mid-edit, no trash-folder watch, no `/api/skills*` changes).

## Global Constraints

- No new SSE route; the wire frame stays a bare topic string (`skills`) on `/api/events/memories`.
- Notifications are fire-and-forget everywhere: a failing notification or scan never fails a mutation.
- The watcher runs on the dashboard **owner** only; MCP/peer processes use the HTTP relay.
- Watcher tick is 1000 ms by default and injectable for tests.
- No real catalog or skill names in tests, fixtures, docs, or comments (neutral names like `fixture-*-skill`, `external-helper`).
- No change to `/api/skills*` REST contracts or DTOs.
- Frontend: verify with `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit` plus vitest; live check on 4427 in the browser; never `next build` while the dev server runs.
- Backend: build/test with `--artifacts-path .bin-test`; never kill the 4417 dev backend.
- Repo hygiene: `.eling/memories` and runtime artifacts stay untracked; commits split per layer (core / backend / frontend), Conventional Commits, hooks mandatory.

## Review Focus

Failure modes the spec implies but no single task's happy-path test pins, most likely first:

1. **External skill added on disk is invisible because the index was never rescanned.** The watcher exists precisely for this. Pin: `SkillCatalogDirWatcher` test asserts the new folder lands in the index (via the scanner it invokes) *before* the `skills` frame fires — Task 2.
2. **A write lands during `ScanAsync()` and is never seen.** Clear-before-work: the drain loop clears `_dirty` before scanning, so a mid-scan write re-sets it and the next tick scans again. Pin: a scanner stub that writes a new skill file inside the first `ScanAsync()` call, expecting at least two broadcasts — Task 2.
3. **Continuous writes starve the refresh.** The fixed 1000 ms tick guarantees at most one scan per tick regardless of event rate. Pin: burst of rapid writes, assert exactly one broadcast arrives per drain and the stream converges — Task 2 (timing-tolerant assertion with 50 ms injected interval).
4. **Silent reload mid-edit destroys the user's form.** The panel is keyed by skill id and keeps local state; a `skills` frame must not remount it or clear fields. Pin: frontend test opens edit mode, fires the SSE frame, asserts typed text survives — Task 3.
5. **Peer-process mutations never reach the owner.** The MCP host is a separate DI container sharing the machine-global catalog; only the HTTP relay crosses the process boundary. Pin: `McpHostBuilder` registers `HttpCoordinatorSkillChangeNotifier` (asserted by code review in Task 1, covered by the owner-adapter test on the receiving end), and a backend test asserts `ISkillService.SaveAsync` actually invokes `ISkillChangeNotifier` — Task 1.

## File Structure

- Create: `src/backend/Eling.Core/Skill/SkillChangeTopics.cs` — the single `SkillChangeTopics.Skills` constant both hosts and the watcher use.
- Create: `src/backend/Eling.Backend/Services/BroadcasterSkillChangeNotifier.cs` — owner in-proc `ISkillChangeNotifier` → `MemoryChangeBroadcaster.Notify(SkillChangeTopics.Skills)`.
- Create: `src/backend/Eling.Backend/Services/HttpCoordinatorSkillChangeNotifier.cs` — MCP-host fire-and-forget HTTP relay (mirrors `HttpCoordinatorCommitLeaseChangeNotifier`).
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` — register `ISkillChangeNotifier` (in-proc adapter) after the existing `shared != null` block; register `SkillCatalogDirWatcher` inside `isOwnerMode`.
- Modify: `src/backend/Eling.Backend/Bootstrap/McpHostBuilder.cs` — register `ISkillChangeNotifier` → HTTP relay next to the commit-lease relay.
- Create: `src/backend/Eling.Backend/SkillCatalogDirWatcher.cs` — the watcher (dirty flag + 1000 ms drain).
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` — `GetService<SkillCatalogDirWatcher>()?.Start();` next to the other watchers.
- Modify: `src/frontend/Eling.Dashboard/src/hooks/use-memories-sse.ts` — add `"skills"` to `SseTopic`.
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/skills/SkillsList.tsx` — subscribe once with `["skills"]`; refetch list + bump `detailNonce`; `detailNonce` joins the detail-fetch effect deps.
- Test: `tests/Eling.Backend.Tests/SkillChangeNotifierTests.cs`, `tests/Eling.Backend.Tests/SkillCatalogDirWatcherTests.cs`; extend `src/frontend/Eling.Dashboard/src/app/dashboard/skills/skills.test.tsx` (or add `skills-sse.test.tsx`).

---

### Task 1: The `skills` topic end-to-end from the mutation side

**Files:**
- Create: `src/backend/Eling.Core/Skill/SkillChangeTopics.cs`
- Create: `src/backend/Eling.Backend/Services/BroadcasterSkillChangeNotifier.cs`
- Create: `src/backend/Eling.Backend/Services/HttpCoordinatorSkillChangeNotifier.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` (after the `shared != null` block, ~line 100; and inside `isOwnerMode` for Task 2's watcher registration stays in Task 2)
- Modify: `src/backend/Eling.Backend/Bootstrap/McpHostBuilder.cs` (after the `ICommitLeaseChangeNotifier` registration, ~line 148)
- Test: `tests/Eling.Backend.Tests/SkillChangeNotifierTests.cs`

**Interfaces:**
- Produces: `public static class SkillChangeTopics` with `public const string Skills = "skills";` (namespace `Eling.Core.Skill`); `BroadcasterSkillChangeNotifier(IMemoryChangeBroadcaster-compatible param: MemoryChangeBroadcaster)` implementing `Eling.Core.Skill.ISkillChangeNotifier`; `HttpCoordinatorSkillChangeNotifier(int port)` implementing the same.
- Consumes: `SkillService.NotifyAsync` (already calls `ISkillChangeNotifier` on saved/renamed/deleted/restored/adopted/rebuilt), `MemoryChangeBroadcaster.Notify(string)` (in-process, owner only), `POST /api/coordinator/notify-change` `[FromBody] string? reason` → verbatim topic (already implemented).

- [ ] **Step 1: Write the failing test** — `tests/Eling.Backend.Tests/SkillChangeNotifierTests.cs`, namespace `Eling.Backend.Tests`:

```csharp
[Fact]
public async Task BroadcasterSkillChangeNotifier_NotifyAsync_BroadcastsSkillsTopic()
{
    var broadcaster = new MemoryChangeBroadcaster();
    var notifier = new BroadcasterSkillChangeNotifier(broadcaster);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var received = ReceiveOneAsync(broadcaster, cts.Token);
    await notifier.NotifyAsync("saved");

    Assert.Equal(SkillChangeTopics.Skills, await received);
}

[Fact]
public async Task SaveAsync_InvokesTheWiredSkillChangeNotifier()
{
    // Core graph, temp catalog: SkillService with a recording notifier.
    // Asserts SkillService really fires on save — the assumption the whole
    // relay rests on.
    var recording = new RecordingNotifier();
    var service = new SkillService(new FileSystemSkillStorage(_catalogRoot), new SqliteSkillIndex(_dbPath), recording);
    await service.SaveAsync(new SkillSaveInput("fixture-skill", "desc", "# body"));
    Assert.True(recording.Calls >= 1);
}

private sealed class RecordingNotifier : ISkillChangeNotifier
{
    public int Calls;
    public Task NotifyAsync(string reason = "mutation", CancellationToken ct = default) { Calls++; return Task.CompletedTask; }
}
```

(`ReceiveOneAsync` mirrors `AuditDirWatcherTests.ReceiveFirstAsync` — `await foreach` over `broadcaster.SubscribeAsync(cts.Token)`, return the first frame. Set up the temp catalog root and db path in an `IAsyncLifetime` fixture like `SkillServiceTests` / `SkillCatalogScannerTests` do: temp dirs, `--artifacts-path .bin-test`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~SkillChangeNotifierTests" --artifacts-path .bin-test`
Expected: FAIL — types do not exist. (The `SkillService` half may already pass; that is fine — it is a regression pin.)

- [ ] **Step 3: Implement**

`src/backend/Eling.Core/Skill/SkillChangeTopics.cs`:
```csharp
namespace Eling.Core.Skill;

/// <summary>
/// The skill-catalog change topic on the shared change stream
/// (/api/events/memories). Single constant so the owner's in-proc adapter,
/// the MCP host's HTTP relay, the catalog watcher and the frontend all agree
/// on the wire value.
/// </summary>
public static class SkillChangeTopics
{
    public const string Skills = "skills";
}
```

`src/backend/Eling.Backend/Services/BroadcasterSkillChangeNotifier.cs`:
```csharp
namespace Eling.Backend.Services;

/// <summary>
/// Owner-side in-proc ISkillChangeNotifier: skill mutations on this process
/// push the "skills" topic straight to SSE subscribers. Mirrors how the
/// shared MemoryChangeBroadcaster serves IMemoryChangeNotifier on the owner.
/// </summary>
public sealed class BroadcasterSkillChangeNotifier(MemoryChangeBroadcaster broadcaster) : ISkillChangeNotifier
{
    public Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default)
    {
        broadcaster.Notify(SkillChangeTopics.Skills);
        return Task.CompletedTask;
    }
}
```
(Add `using Eling.Core.Skill;` as needed.)

`src/backend/Eling.Backend/Services/HttpCoordinatorSkillChangeNotifier.cs` — structurally identical to `HttpCoordinatorCommitLeaseChangeNotifier`, with `public const string SkillTopic = SkillChangeTopics.Skills;`, posting `new StringContent(JsonSerializer.Serialize(SkillTopic), Encoding.UTF8, "application/json")` to `/api/coordinator/notify-change`, `Timeout = 2s`, catch-and-log-Debug. Keep the class doc comment stating why (peer serves no SSE; the relay is the only channel to the port owner; self-loopback when this process owns the port — so no re-wiring on takeover).

Register in `DashboardServices.Register`, after the `if (shared != null) { ... } else { ... }` block that registers `IMemoryChangeNotifier` (around line 100):
```csharp
// eling-mediated skill mutations must reach the owner's SSE stream no
// matter which container serves them; the owner has an in-proc path.
services.AddSingleton<ISkillChangeNotifier>(sp =>
    new BroadcasterSkillChangeNotifier(sp.GetRequiredService<MemoryChangeBroadcaster>()));
```
Register in `McpHostBuilder`, after the `ICommitLeaseChangeNotifier` registration (around line 148):
```csharp
// Skill mutations from this (possibly peer) process: same cross-process
// story as memory writes — the POST relays "skills" to the port owner.
services.AddSingleton<ISkillChangeNotifier>(
    _ => new HttpCoordinatorSkillChangeNotifier(effectivePort));
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~SkillChangeNotifierTests" --artifacts-path .bin-test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Skill/SkillChangeTopics.cs src/backend/Eling.Backend/Services/BroadcasterSkillChangeNotifier.cs src/backend/Eling.Backend/Services/HttpCoordinatorSkillChangeNotifier.cs src/backend/Eling.Backend/Bootstrap/DashboardServices.cs src/backend/Eling.Backend/Bootstrap/McpHostBuilder.cs tests/Eling.Backend.Tests/SkillChangeNotifierTests.cs
git commit -m "feat(skills): relay skill-change events to the owner SSE stream"
```

---

### Task 2: `SkillCatalogDirWatcher` — the disk half of approach C

**Files:**
- Create: `src/backend/Eling.Backend/SkillCatalogDirWatcher.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` (`isOwnerMode` block, after the `DatabaseRegistryWatcher` registration, ~line 200)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` (after `app.Services.GetService<DatabaseRegistryWatcher>()?.Start();`, ~line 53)
- Test: `tests/Eling.Backend.Tests/SkillCatalogDirWatcherTests.cs`

**Interfaces:**
- Consumes: `ISkillCatalogScanner.ScanAsync() : Task<ScanReport>`; `SkillCatalogPaths.SkillsDirectory`; `MemoryChangeBroadcaster.Notify(string)`; `SkillChangeTopics.Skills`.
- Produces: `public sealed class SkillCatalogDirWatcher : IDisposable` with ctor `(ISkillCatalogScanner scanner, MemoryChangeBroadcaster broadcaster, string? skillsDirectory = null, TimeSpan? interval = null, ILogger<SkillCatalogDirWatcher>? logger = null)`, `public const string Topic = SkillChangeTopics.Skills;`, `Start()`, `Dispose()`. Registered only in owner mode; `Start()` called from `DashboardRoutes.Map` like the other watchers.

- [ ] **Step 1: Write the failing test** — `tests/Eling.Backend.Tests/SkillCatalogDirWatcherTests.cs` (mirror `AuditDirWatcherTests`; fixture: temp `_root` with `_root\skills`, temp `_dbPath`, real `SqliteSkillIndex(_dbPath)` + real `SkillCatalogScanner(index, _root)`, `MemoryChangeBroadcaster`, `new SkillCatalogDirWatcher(scanner, broadcaster, skillsDirectory: _root\skills, interval: 50ms)`; clean up dirs in `DisposeAsync`):

```csharp
[Fact]
public async Task NewSkillFolder_IsIndexedAndBroadcastsSkillsTopic()
{
    var received = ReceiveTopicAsync(TimeSpan.FromSeconds(10));
    // A neutral external-style skill folder appears on disk, unseen by any eling API.
    var folder = Path.Combine(SkillsDir, "external-helper");
    Directory.CreateDirectory(folder);
    await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), "---\nname: external-helper\ndescription: supplied by another tool\n---\n# external-helper\n");

    Assert.Equal(SkillChangeTopics.Skills, await received);
    // The rescan ran before the ping: the row is now in the index.
    var row = await _index.GetByNameAsync("external-helper");
    Assert.NotNull(row);
}

[Fact]
public async Task ChangeDuringScan_TriggersASecondBroadcast()
{
    // A scanner stub that drops a new skill file into the watched dir on its
    // first ScanAsync() call must cause the watcher to see the write and
    // scan again — clear-before-work.
    var received = ReceiveTopicsAsync(expectedCount: 2, TimeSpan.FromSeconds(10));
    _watcher = new SkillCatalogDirWatcher(new ScanThenWriteScanner(inner, SkillsDir), broadcaster, SkillsDir, TimeSpan.FromMilliseconds(50));
    _watcher.Start();
    await TriggerAnyWrite();
    Assert.Equal(2, (await received).Count);   // one from the trigger, one from the mid-scan write
}
```

(`ReceiveTopicAsync` collects frames from `broadcaster.SubscribeAsync(cts.Token)` until timeout/count; `TriggerAnyWrite` creates one neutral folder. The `ScanThenWriteScanner : ISkillCatalogScanner` wraps the real scanner and, on the first call, first creates `skills\mid-scan-helper\SKILL.md` then delegates. Keep fixtures neutral.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~SkillCatalogDirWatcherTests" --artifacts-path .bin-test`
Expected: FAIL — watcher does not exist.

- [ ] **Step 3: Implement `SkillCatalogDirWatcher`**

Structure (mirror `AuditDirWatcher` for shape, `CodebaseWatcherService` for the drain loop, both already in the repo):
- `volatile int _dirty;` events (`Created`/`Changed`/`Deleted`/`Renamed` on `LastWrite | FileName | DirectoryName`, recursive, plus `Error` → set flag + log warning) set `_dirty = 1` and return immediately.
- `Start()` (idempotent, logs like the other watchers): creates the `FileSystemWatcher` over `skillsDirectory`, enables it, tries `Directory.CreateDirectory` if missing, then starts an `async` drain loop on an internal `CancellationTokenSource`:
```csharp
using var timer = new PeriodicTimer(_interval);
try
{
    while (await timer.WaitForNextTickAsync(_cts.Token))
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) continue;   // clear BEFORE work
        try { await _scanner.ScanAsync(); await _broadcaster.NotifyAsync(Topic); }
        catch (Exception ex) { _logger.LogWarning(ex, "Skill catalog rescan failed; the next event will retry."); }
    }
}
catch (OperationCanceledException) { }
```
Clear-before-work is the load-bearing line: a write landing mid-scan re-sets `_dirty` and the next tick rescans — nothing is lost.
- `Dispose()`: disable events, dispose watcher, cancel + dispose the loop CTS (swallow `ObjectDisposedException`).
- `MemoryChangeBroadcaster` has `NotifyAsync` — if you prefer a plain call, use `Notify(Topic)` like the other watchers.

Register in `DashboardServices.Register` inside `if (isOwnerMode)` after the `DatabaseRegistryWatcher` block (~line 200):
```csharp
// Machine-global catalog: any process (or external tool) can write it, so
// the owner reconciles the index on a 1 s dirty drain and pushes "skills".
services.AddSingleton(sp => new SkillCatalogDirWatcher(
    sp.GetRequiredService<ISkillCatalogScanner>(),
    sp.GetRequiredService<MemoryChangeBroadcaster>(),
    SkillCatalogPaths.SkillsDirectory,
    logger: sp.GetService<ILogger<SkillCatalogDirWatcher>>()));
```
(`using Eling.Core.Scope;` is already present; confirm `ISkillCatalogScanner` is resolvable — it is registered in `AddSkillServices`, called by both core-service overloads.) Add in `DashboardRoutes.Map`:
```csharp
app.Services.GetService<SkillCatalogDirWatcher>()?.Start();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~SkillCatalogDirWatcherTests" --artifacts-path .bin-test`
Expected: PASS (allow ~2 s; injected 50 ms tick).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Backend/SkillCatalogDirWatcher.cs src/backend/Eling.Backend/Bootstrap/DashboardServices.cs src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs tests/Eling.Backend.Tests/SkillCatalogDirWatcherTests.cs
git commit -m "feat(skills): watch the catalog folder and broadcast skills changes"
```

---

### Task 3: Frontend — subscribe and silent-reload

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/hooks/use-memories-sse.ts`
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/skills/SkillsList.tsx`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/skills/skills-sse.test.tsx` (new; keep the existing `skills.test.tsx` untouched)

**Interfaces:**
- Consumes: `useMemoriesSse(onMutation, onRuntimesChange?, topics)` from Task 1 (hook exists; you add a topic value), `SkillsList`'s local `load()` (existing `useCallback`) and `selectedId`.
- Produces: `SseTopic` union including `"skills"`; `SkillsList` subscribed with `["skills"]`; detail-fetch effect keyed on `[selectedId, detailNonce]`.

- [ ] **Step 1: Write the failing test** — `skills-sse.test.tsx` (stub `fetch` exactly like `skills.test.tsx`; stub `EventSource` like the commit-lease page test):

```tsx
it("a skills SSE frame refetches the list and re-fetches the open detail, silently", async () => {
  render(<SkillsList />)
  expect(await screen.findByText("fixture-eling-skill")).toBeTruthy()
  const fetchCallsBefore = (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls.length

  // open the detail panel, then fire a skills frame
  fireEvent.click(screen.getByText("fixture-eling-skill"))
  const es = lastStubEventSource()            // capture via the stub class
  act(() => es.onmessage?.({ data: "skills" } as MessageEvent))

  await waitFor(() =>
    expect((fetch as unknown as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(fetchCallsBefore + 1))
  // panel is still open with the same skill (no remount, no vanish)
  expect(screen.getByText("fixture-eling-skill")).toBeTruthy()
})

it("a skills frame mid-edit keeps the typed text", async () => {
  render(<SkillsList />)
  fireEvent.click(await screen.findByText("fixture-eling-skill"))
  fireEvent.click(await screen.findByRole("button", { name: /^edit$/i }))
  const desc = await screen.findByLabelText(/description/i)
  fireEvent.change(desc, { target: { value: "my in-progress edit" } })
  act(() => lastStubEventSource().onmessage?.({ data: "skills" } as MessageEvent))
  await waitFor(() => expect((desc as HTMLInputElement).value).toBe("my in-progress edit"))
})
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `pnpm --dir src/frontend/Eling.Dashboard exec vitest run src/app/dashboard/skills/skills-sse.test.tsx`
Expected: FAIL — no EventSource subscription / `SseTopic` lacks `skills`.

- [ ] **Step 3: Implement**

`use-memories-sse.ts`: add `| "skills"` to the `SseTopic` union (keep default topics unchanged).

`SkillsList.tsx`:
```tsx
// after selectedId is derived:
const [detailNonce, setDetailNonce] = useState(0)
const handleSkillsChanged = useCallback(() => {
  // Silent: no loading flash beyond the LoadingBar, no panel close.
  void load()
  setDetailNonce((n) => n + 1)
}, [load])
useMemoriesSse(handleSkillsChanged, undefined, ["skills"])
```
and change the detail-fetch effect's dependency array from `[selectedId]` to `[selectedId, detailNonce]`. Nothing else moves — the panel stays keyed by skill id, so edit-form state survives the silent refetch (verified by the test above). Import `useMemoriesSse` from `@/hooks/use-memories-sse`.

- [ ] **Step 4: Run the test to verify it passes**

Run: `pnpm --dir src/frontend/Eling.Dashboard exec vitest run src/app/dashboard/skills`
Expected: PASS (existing `skills.test.tsx` + new `skills-sse.test.tsx`).

- [ ] **Step 5: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/hooks/use-memories-sse.ts src/frontend/Eling.Dashboard/src/app/dashboard/skills/SkillsList.tsx src/frontend/Eling.Dashboard/src/app/dashboard/skills/skills-sse.test.tsx
git commit -m "feat(dashboard): live refresh of the skills page via the skills SSE topic"
```

---

### Task 4: Whole-branch verification

**Files:** none — verification only.

- [ ] **Step 1: Backend lite suite**

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~Skill|FullyQualifiedName~AuditDirWatcher|FullyQualifiedName~MemoryApi" --artifacts-path .bin-test`
Expected: PASS. (Do NOT run the full suite on this machine if the known temp-file flake appears in unrelated tests — record it and move on.)

- [ ] **Step 2: Frontend typecheck + tests**

Run: `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit` and `pnpm --dir src/frontend/Eling.Dashboard exec vitest run src/app/dashboard/skills`
Expected: clean / PASS.

- [ ] **Step 3: Live check on 4427** (frontend dev server must already be running; never `next build`)

Open `http://localhost:4427/dashboard/skills` in the browser:
1. Edit a skill body via the MCP `skill_save` tool from another session → the open detail body updates within ~1 s, no reload.
2. `mkdir` + write a neutral `SKILL.md` under `~/.agents/skills/<name>/` → the new card appears within ~2 s (watcher rescan).
3. Trash it via the UI → list updates; detail shows the trash notice.
4. Type into the Edit form, then trigger any of the above → typed text survives.

- [ ] **Step 4: Report** — summarize what shipped, what deviated from the spec (watcher landed as `IDisposable` + `Start()` like `AuditDirWatcher`, watching `SkillsDirectory`, tick injectable — already reflected in the spec), and anything left open.
