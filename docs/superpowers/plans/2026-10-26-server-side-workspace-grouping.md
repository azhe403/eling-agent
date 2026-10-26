# Server-Side Workspace Grouping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the coordinator the single owner of the workspace list's arrangement. Today three copies of the same grouping rules exist (`src/frontend/Eling.Dashboard/src/components/workspaces/grouping.ts`, `src/desktop/Eling.Desktop/Services/WorkspaceGrouping.cs`, `src/desktop/Eling.Desktop/Services/MemoryWorkspaceTree.cs`) and drift apart. After this plan the rules live once, in `Eling.Core`, and both clients render a tree the backend hands them.

**Architecture:** `GET /api/coordinator/workspaces` returns the workspace rows plus **all three arrangements** (`flat`, `repo`, `folder`) in one response, so switching the view never costs a request and neither client needs a per-mode cache. The endpoint derives rows from `RuntimeRegistry` (the rules `workspaceRowsOf` and `MainViewModel.RefreshWorkspacesAsync` currently duplicate) and applies the `status`/`q` filter before grouping. Clients keep their view models and checkbox state; only the tree *shape* comes from the server. `WorkspaceGrouping.cs`, `MemoryWorkspaceTree.Build`, and `buildWorkspaceTree` are deleted.

**Tech Stack:** .NET 10 (ASP.NET Core), `Eling.Core` (no dependencies), System.Text.Json, xUnit. Dashboard: Next.js 16, React 19, TypeScript. Desktop: Avalonia 12 + ReactiveUI.

**Spec:** `docs/superpowers/specs/2026-10-26-server-side-workspace-grouping.md` (design doc, `draft`). This plan deviates from the design doc in one place, deliberately: the endpoint returns three trees instead of taking a `group` parameter — the design's per-mode caching disappears, and a mode toggle stops being a round-trip. Note the deviation in the design doc's status line when the plan lands.

## Global Constraints

- **No behavior change in what the list shows.** The ported rules must reproduce the current output exactly: path comparison lowercases and normalizes separators, a repository's entry is labelled by its main checkout, a repository with more than one checkout starts expanded, entries sort by label.
- `Eling.Core` gains no new dependency. Grouping is pure: rows in, nodes out, no I/O, no `DateTime`, no environment.
- Rows are **deduped per workspace** (case-insensitive on root). Two live processes in one folder are one row.
- Rows exclude the `UserScope` sentinel and runtimes with `CodebaseEnabled == false`.
- The endpoint is read-only and must not write to the registry, the codebase index, or `.eling`.
- Conventional commits: `feat(backend): ...`, `feat(dashboard): ...`, `feat(desktop): ...`, `refactor(...)` for deletions.
- `q` matches the workspace's display name, its root, **or any owning process id** — the desktop Folders search matches PIDs today and must keep working.

## Review Focus

- **The ported rules are the whole risk.** `WorkspaceGrouping.Build` (C#) and `buildWorkspaceTree` (TS) are near-identical but not identical: the TS folder mode sorts merged children, the C# one sorts `RepositoryNode` children then appends nested groups. Port from the TS version, which is the more recent one, and pin the folder-mode ordering in a test. Diff the outputs on the real workspace list before deleting either copy.
- **Row derivation is a silent contract.** If the endpoint skips a runtime the client used to show, a workspace disappears from the list with no error. Test `UserScope` exclusion, `CodebaseEnabled == false` exclusion, root fallback (`WorkspaceRoot` else `HeadScopeRoot`), and dedup explicitly.
- **Two checkouts of one repository in one folder tree.** Folder mode nests a repository under the nearest ancestor *repository*, not the nearest ancestor folder. A repo inside a repo is the case that breaks a naive port.
- **Checkbox identity survives.** Selection is by root string on both clients. The mapper must reuse the existing `WorkspaceItem`/`WorkspaceRow` instances so a ticked box stays ticked after a refresh.
- **Desktop Folders filter becomes a request.** Per-keystroke requests are unacceptable; the filter is debounced and the previous request cancelled. The flat list is joined back to the aggregated items so process metrics keep rendering.

---

### Task 1: Grouping rules in `Eling.Core`

**Files:**
- Create: `src/backend/Eling.Core/Workspaces/WorkspaceGroupMode.cs`
- Create: `src/backend/Eling.Core/Workspaces/WorkspaceTreeRow.cs`
- Create: `src/backend/Eling.Core/Workspaces/WorkspaceTreeNode.cs`
- Create: `src/backend/Eling.Core/Workspaces/WorkspaceTree.cs`
- Test: `tests/Eling.Core.Tests/WorkspaceTreeTests.cs`

**Interfaces:**
- Consumes: nothing (pure).
- Produces:
  - `enum WorkspaceGroupMode { Flat, Repo, Folder }` — order matters: the desktop settings store persists the value as an int.
  - `sealed record WorkspaceTreeRow(string Root, bool Alive, string? RepositoryRoot)`
  - `sealed record WorkspaceTreeNode(string Id, string Label, string Path, string? Root, bool Alive, IReadOnlyList<string> Roots, bool IsRepository, bool IsWorktree, IReadOnlyList<WorkspaceTreeNode> Children)`
  - `static class WorkspaceTree { public static IReadOnlyList<WorkspaceTreeNode> Build(IReadOnlyList<WorkspaceTreeRow> rows, WorkspaceGroupMode mode); }`
  - Node id format is preserved from both current copies: `ws:{pathKey}` for a workspace, `repo:{pathKey}` for a repository.

- [ ] **Step 1: Write the failing tests.** Port every assertion currently in `tests/Eling.Desktop.Tests/MemoryWorkspaceTreeTests.cs` that concerns *shape* (not expansion) to `tests/Eling.Core.Tests/WorkspaceTreeTests.cs`, plus the folder-mode cases the desktop suite never covered:

  - `Build_FlatMode_SortsByLabelAndKeepsEveryRow`
  - `Build_RepoMode_GroupsWorktreesUnderTheirMainCheckout` — group of 2, `IsRepository`, `Root` is the main checkout, child `IsWorktree`, child `Root` is the worktree.
  - `Build_RepoMode_WithoutRegisteredMainCheckout_UsesRepositoryRootAsPath` — only the worktree is registered; path/label come from `RepositoryRoot`, `Root` is null.
  - `Build_RepoMode_AggregatesAliveness` — one member alive makes the node alive.
  - `Build_FolderMode_NestsARepositoryInsideAnotherRepository`
  - `Build_FolderMode_SortsChildrenByLabel`
  - `Build_EmptyInput_ReturnsNoNodes`
  - `Build_PathComparison_IgnoresSeparatorsAndCase` — `C:\Repo` and `c:/repo` land in one bucket.

  Run: `dotnet test tests/Eling.Core.Tests --filter FullyQualifiedName~WorkspaceTreeTests` → fails to compile (types absent). That is the red step.

- [ ] **Step 2: Implement `WorkspaceTree`.** Port from `src/frontend/Eling.Dashboard/src/components/workspaces/grouping.ts` (the newer copy), keeping the C# idiom of `src/desktop/Eling.Desktop/Services/WorkspaceGrouping.cs`:

  - `PathKey`: `Replace('\\', '/')`, trim trailing `/`, `ToLowerInvariant()`.
  - `IsAncestor`: `d.StartsWith(a + "/", Ordinal)` and `a != d`.
  - `RepositoryGroups`: bucket by `PathKey(RepositoryRoot ?? Root)`; primary is the member whose own root key equals the bucket key; path is `primary?.Root ?? members[0].RepositoryRoot ?? members[0].Root`.
  - `WorkspaceNode`: id `ws:{key}`, label is the last path segment of the trimmed root with the root as fallback, `Roots = [root]`, `IsWorktree = false`.
  - `RepositoryNode`: id `repo:{key}`, children are the non-primary members as worktree nodes sorted by label, `Roots` is every member root in bucket order, `Alive` is `members.Any(m => m.Alive)`.
  - `Folder` mode: for each repository group find the nearest ancestor *repository group* (`IsAncestor(candidate.Path, group.Path)`, deepest wins), nest, sort each node's children by label, return the unnested roots sorted by label.
  - Flat mode: every row as a node, sorted by label.

  Keep the private helpers private; the only public surface is `Build`.

- [ ] **Step 3: Run the tests green.** `dotnet test tests/Eling.Core.Tests --filter FullyQualifiedName~WorkspaceTreeTests`

- [ ] **Step 4: Commit.** `feat(core): add workspace tree grouping`

---

### Task 2: `GET /api/coordinator/workspaces`

**Files:**
- Create: `src/backend/Eling.Backend/Dtos/WorkspaceTreesDto.cs`
- Edit: `src/backend/Eling.Backend/Controllers/CoordinatorController.cs`
- Test: `tests/Eling.Backend.Tests/WorkspaceTreeControllerTests.cs`

**Interfaces:**
- Consumes: `RuntimeRegistry.AliveOrRegisteredAsync(ct)` (deduped, includes remembered workspaces), `GitRepositoryResolver.ResolveRepositoryRoot`, `Eling.Core.Workspaces.WorkspaceTree`.
- Produces:
  ```csharp
  public sealed record WorkspaceTreesDto(
      IReadOnlyList<WorkspaceTreeRowDto> Rows,
      IReadOnlyList<WorkspaceTreeNodeDto> Flat,
      IReadOnlyList<WorkspaceTreeNodeDto> Repo,
      IReadOnlyList<WorkspaceTreeNodeDto> Folder);

  public sealed record WorkspaceTreeRowDto(
      string Root, string Name, string? RepositoryRoot, bool Alive,
      bool CodebaseEnabled, int ProcessCount);

  public sealed record WorkspaceTreeNodeDto(
      string Id, string Label, string Path, string? Root, bool Alive,
      IReadOnlyList<string> Roots, bool IsRepository, bool IsWorktree,
      IReadOnlyList<WorkspaceTreeNodeDto> Children);
  ```
  Route: `GET api/coordinator/workspaces?status=all|active|offline&q=<text>`. `status` defaults to `all`; anything else → `400`. `q` is trimmed, case-insensitive, matches name, root, or any owning process id. Serialized camelCase (the app's default).

- [ ] **Step 1: Write the failing tests** in `tests/Eling.Backend.Tests/WorkspaceTreeControllerTests.cs`, copying the harness from `CoordinatorControllerTests` (same `WebApplication.CreateBuilder` + `UseTestServer` + `AddApplicationPart` setup, same `ElingDataDirCollection` collection):

  - `Rows_ExcludesUserScopeAndDisabledCodebase` — register a normal workspace, a `UserScope` runtime, and one with `CodebaseEnabled = false`; only the first appears.
  - `Rows_DedupesTwoProcessesInOneFolder` — two registrations, same `WorkspaceRoot`, different `ProcessId` → one row.
  - `Rows_FallsBackToHeadScopeRoot` — `WorkspaceRoot` null → row root is `HeadScopeRoot`.
  - `Status_Active_FiltersOfflineWorkspaces` — one live, one remembered; `?status=active` returns one row and a repo tree with one member.
  - `Query_MatchesFolderNameAndProcessId` — `?q=` with a substring of the folder name, and separately the PID as a string.
  - `Response_CarriesAllThreeArrangements` — `flat`, `repo`, `folder` all non-empty and consistent with `rows`.
  - `Status_Unknown_ReturnsBadRequest`

  Run: `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~WorkspaceTreeControllerTests` → 404s / compile errors.

- [ ] **Step 2: Implement the endpoint** in `CoordinatorController`:

  ```csharp
  [HttpGet("workspaces")]
  public async Task<ActionResult<WorkspaceTreesDto>> GetWorkspacesAsync(
      [FromQuery] string? status = "all",
      [FromQuery] string? q = null,
      CancellationToken cancellationToken = default)
  ```

  - `RowsOf(runtimes)` — private static, the rules lifted from `MainViewModel.RefreshWorkspacesAsync` and `workspaceRowsOf`: skip blank root, skip `UserScope` (ordinal-ignore-case), skip `CodebaseEnabled == false`, root is `WorkspaceRoot` else `HeadScopeRoot`, dedupe on `StringComparer.OrdinalIgnoreCase`, `RepositoryRoot` from the existing `ResolveRepositoryRoot`. Each row also carries `Name` (last path segment of the trimmed root, root as fallback, matching `workspaceName` in `runtime-rows.ts`), `CodebaseEnabled` (always true after filtering), and `ProcessCount` (number of live processes that collapsed into this row).
  - Filter after dedup, before grouping, so a filtered-out workspace cannot pull its repository into the tree.
  - For the PID match, keep the runtime's `ProcessId` alongside the row while filtering (a small private tuple list) rather than widening the DTO.
  - Map `Eling.Core.Workspaces.WorkspaceTreeNode` → `WorkspaceTreeNodeDto` with one recursive local function.
  - Reuse `ResolveRepositoryRoot`; do not add a second git-metadata reader.

- [ ] **Step 3: Run green.** `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~WorkspaceTreeControllerTests`

- [ ] **Step 4: Manual check against the live list.** With the dev backend up on 4417, compare `curl "http://127.0.0.1:4417/api/coordinator/workspaces"` with the dashboard's current tree for a repository that has a main checkout plus at least one worktree. Same grouping, same order, same labels.

- [ ] **Step 5: Commit.** `feat(backend): serve the workspace tree from the coordinator`

---

### Task 3: Dashboard reads the tree instead of building it

**Files:**
- Edit: `src/frontend/Eling.Dashboard/src/components/workspaces/runtime-rows.ts`
- Edit: `src/frontend/Eling.Dashboard/src/components/workspaces/grouping.ts`
- Edit: `src/frontend/Eling.Dashboard/src/components/workspaces/use-workspace-rows.ts`
- Edit: `src/frontend/Eling.Dashboard/src/components/workspaces/workspace-tree.tsx`
- Edit: `src/frontend/Eling.Dashboard/src/components/workspaces/workspace-select.tsx`
- Edit: `src/frontend/Eling.Dashboard/src/components/workspaces/workspace-scope-panel.tsx`

**Interfaces:**
- Consumes: `GET /api/coordinator/workspaces`.
- Produces:
  - `type WorkspaceTreeNode = { id, label, path, root: string | null, alive: boolean, roots: readonly string[], isRepository: boolean, isWorktree: boolean, children: readonly WorkspaceTreeNode[] }` — replaces `WorkspaceNode`, whose `row`/`group` of `WorkspaceRow` are gone.
  - `fetchWorkspaceTrees(): Promise<WorkspaceTreesResponse>` in `runtime-rows.ts`, tolerant of missing fields like `fetchRuntimes`.
  - `useWorkspaceTrees(): { rows, trees, error, reload }` and `useWorkspaceTree(mode): WorkspaceTreeNode[]` in `use-workspace-rows.ts`. Both read one module-level cache with in-flight de-duplication, so a page and a tree mounted together issue one request.
  - `WorkspaceTree` takes no `rows` prop any more: it calls `useWorkspaceTrees()` + `useWorkspaceGroupMode()` itself.

- [ ] **Step 1: Add the payload type and fetch** in `runtime-rows.ts` next to `fetchRuntimes`. Reuse the same defensive reading (`?? null`, `Array.isArray`).

- [ ] **Step 2: Reduce `grouping.ts` to types.** Delete `pathKey`, `isAncestor`, `repositoryKey`, `repositoryGroups`, `workspaceNode`, `repositoryNode`, `byLabel`, `flatNodes`, `repositoryNodes`, `folderNodes`, `buildWorkspaceTree`. Keep `WorkspaceGroupMode`, the node type, and a `workspaceTreesOf(response)` mapper that picks `flat`/`repo`/`folder`. Rewrite the file header: the arrangement now comes from the coordinator; this file only names its shape.

- [ ] **Step 3: Extend the hook.** Keep `cachedRows`/`cachedError`, add `cachedTrees` and an `inFlight` promise so two mounts share one request. `useWorkspaceRows()` keeps its current signature (`{ rows, error, reload }`) so `MemoriesList.tsx` and `CodebaseList.tsx` need no change. `useWorkspaceTree(mode)` derives from the same state — no extra fetch, no refetch on mode change.

- [ ] **Step 4: Rewire `workspace-tree.tsx`.** Drop the `rows` and `mode` props' derivation: `const { trees } = useWorkspaceTrees()`, `const { mode } = useWorkspaceGroupMode()`, `const nodes = trees[mode]`. Then replace the row-shaped reads:

  - `const group = node.group` → remove; use `node.roots`
  - `const row = node.row` → `const root = node.root`
  - `includedCount = group.filter((r) => included.has(r.root)).length` → `roots.filter((r) => included.has(r)).length`
  - `alive = group.some((r) => r.alive)` → `node.alive`
  - `group.length > 1` → `node.roots.length > 1`
  - `onSelectOnly(row.root)` → `onSelectOnly(root)`
  - the `included` set for `selection.kind === "all"` comes from `nodes.flatMap((n) => n.roots)`.
  - the empty state keys off `rows.length === 0` from the hook.

- [ ] **Step 5: Update the two call sites.** `workspace-select.tsx` and `workspace-scope-panel.tsx` stop passing `rows`/`mode` into `WorkspaceTree`; they keep their own `rows` prop for `SingleSelect` and counts.

- [ ] **Step 6: Verify.** `pnpm lint` and `pnpm build` in `src/frontend/Eling.Dashboard`. Then run the dev frontend on 4427 against the dev backend on 4417 and toggle all three modes on the memories and codebase pages: same tree as before, expansion defaults unchanged, ticking a repository ticks its worktrees, and the network panel shows one `workspaces` request per page load (not one per toggle).

- [ ] **Step 7: Commit.** `feat(dashboard): render the workspace tree from the coordinator`

---

### Task 4: Desktop DTOs, client, and node mapping

**Files:**
- Create: `src/desktop/Eling.Desktop/Models/WorkspaceTreesDto.cs`
- Edit: `src/desktop/Eling.Desktop/Services/ElingApiClient.cs`
- Create: `src/desktop/Eling.Desktop/Services/WorkspaceTreeMapper.cs`
- Edit: `src/desktop/Eling.Desktop/Services/MemoryWorkspaceTree.cs`
- Edit: `src/desktop/Eling.Desktop/ViewModels/MainViewModel.cs`
- Test: `tests/Eling.Desktop.Tests/WorkspaceTreeMapperTests.cs`

**Interfaces:**
- Consumes: `GET api/coordinator/workspaces`, `Eling.Core.Workspaces.WorkspaceGroupMode`.
- Produces:
  - `record WorkspaceTreesDto(IReadOnlyList<WorkspaceTreeRowDto> Rows, IReadOnlyList<WorkspaceTreeNodeDto> Flat, IReadOnlyList<WorkspaceTreeNodeDto> Repo, IReadOnlyList<WorkspaceTreeNodeDto> Folder)` with `WorkspaceTreeNodeDto.Children` defaulting to `[]` when absent.
  - `Task<WorkspaceTreesDto?> GetWorkspaceTreesAsync(string? status, string? q, CancellationToken ct = default)` on `ElingApiClient`, returning `null` on failure like its neighbours.
  - `static class WorkspaceTreeMapper`:
    - `List<WorkspaceNode> ToFolderNodes(IReadOnlyList<WorkspaceTreeNodeDto> nodes, Func<string, WorkspaceItemDto?> row)`
    - `List<MemoryWorkspaceNode> ToMemoryNodes(IReadOnlyList<WorkspaceTreeNodeDto> nodes, Func<string, WorkspaceItem?> row)`
    - Both preserve id, label, path, `IsRepository`, `IsWorktree`, child order; `Group` is the rows resolved through the lookup in `Roots` order (unresolvable roots are skipped, never invented); `MemoryWorkspaceNode` starts expanded when it is not a worktree and has more than one root, matching `MemoryWorkspaceTree.Build` today.
  - `MemoryWorkspaceTree` keeps `FlattenVisible`, `CaptureExpansion`, `RestoreExpansion`, `RefreshAggregates`; `Build` and its private helpers are deleted.

- [ ] **Step 1: Write the failing tests** in `tests/Eling.Desktop.Tests/WorkspaceTreeMapperTests.cs`: repository node keeps its worktree child and group order; `Row` is the *same instance* the lookup returned (assert `Assert.Same`); an unknown root is skipped rather than added; a leaf starts collapsed and a multi-root repository starts expanded; flat mode preserves server order.

  Run: `dotnet test tests/Eling.Desktop.Tests --filter FullyQualifiedName~WorkspaceTreeMapperTests` → compile failure.

- [ ] **Step 2: Implement** the DTOs, the client method, and the mapper.

- [ ] **Step 3: Wire `MainViewModel`.** `RefreshWorkspacesAsync` fetches runtimes as today, then `GetWorkspaceTreesAsync(null, null)`, stores the response in a `_trees` field, and calls `RebuildWorkspaceNodes()`. `RebuildWorkspaceNodes` becomes synchronous over `_trees`:

  ```csharp
  MemoryWorkspaceTree.CaptureExpansion(_workspaceRoots, _workspaceExpansion);
  _workspaceRoots = WorkspaceTreeMapper.ToMemoryNodes(_trees[WorkspaceGroupMode], LookupWorkspace);
  MemoryWorkspaceTree.RestoreExpansion(_workspaceRoots, _workspaceExpansion);
  MemoryWorkspaceTree.RefreshAggregates(_workspaceRoots);
  ```

  `WorkspaceGroupMode` becomes `Eling.Core.Workspaces.WorkspaceGroupMode`, so the mode setter keeps working without an async hop and no request is issued on a toggle. When the fetch fails, keep the last good `_trees` rather than blanking the sidebar.

- [ ] **Step 4: Run green.** `dotnet test tests/Eling.Desktop.Tests`

- [ ] **Step 5: Commit.** `feat(desktop): map the coordinator's workspace tree into the memory sidebar`

---

### Task 5: Desktop Folders filters server-side; delete both local implementations

**Files:**
- Edit: `src/desktop/Eling.Desktop/ViewModels/FoldersViewModel.cs`
- Edit: `src/desktop/Eling.Desktop/Services/SettingsStore.cs` (or wherever `SetWorkspaceGroupMode(WorkspaceGrouping.Mode)` lives)
- Delete: `src/desktop/Eling.Desktop/Services/WorkspaceGrouping.cs`
- Edit: `tests/Eling.Desktop.Tests/MemoryWorkspaceTreeTests.cs`

**Interfaces:**
- Consumes: `GetWorkspaceTreesAsync(status, q)`, `WorkspaceTreeMapper.ToFolderNodes`.
- Produces: `FoldersViewModel` with no local grouping and no local filter predicate. `Workspaces` is the server's `Rows` joined to `_allWorkspaces`; `GroupedNodes` is the server's tree for the current mode.

- [ ] **Step 1: Rewrite `MemoryWorkspaceTreeTests`.** Its `Build` cases are now covered by `WorkspaceTreeTests` in Core. Keep only `FlattenVisible`, `CaptureExpansion`/`RestoreExpansion`, and `RefreshAggregates`, constructing `MemoryWorkspaceNode`s directly through a small helper. Run: `dotnet test tests/Eling.Desktop.Tests` → red until `Build` is gone.

- [ ] **Step 2: Replace `ApplyFilter`.** Rename to `ApplyFilterAsync` with a `CancellationTokenSource` field: cancel the previous filter, wait ~150 ms (debounce), fetch, then on the UI thread rebuild `Workspaces` and `GroupedNodes`. `SearchQuery`/`FilterStatus` setters fire-and-forget it. `LoadAsync` awaits it after the aggregator runs.

  Join: `Workspaces` is filled from the response `Rows`, mapping each root to the matching `_allWorkspaces` item (ordinal-ignore-case); a root the aggregator does not know becomes a minimal `WorkspaceItemDto` from the row (`Root`, `IsRunning = Alive`) so nothing the server reports goes invisible. Counters keep coming from the aggregator over the unfiltered set.

- [ ] **Step 3: Switch the mode type.** `FoldersViewModel` and `SettingsStore` use `Eling.Core.Workspaces.WorkspaceGroupMode`; a mode change re-maps `GroupedNodes` from the cached response with no request.

- [ ] **Step 4: Delete `WorkspaceGrouping.cs`.** `grep -rn "WorkspaceGrouping" src/` must return only the settings-store migration note, if any.

- [ ] **Step 5: Verify.** `dotnet build`, `dotnet test`, then run the desktop app: Folders page groups by repository and folder, search by name and by PID still finds the workspace, the status filter still works, and typing does not fire a request per keystroke (watch the backend log).

- [ ] **Step 6: Commit.** `refactor(desktop): take the folder tree and filter from the coordinator`

---

### Task 6: Sweep and document

**Files:**
- Edit: `docs/superpowers/specs/2026-10-26-server-side-workspace-grouping.md` (status → `implemented`, record the three-trees deviation)
- Edit: `AGENTS.md` only if it names a grouping owner (it does not today — skip if unchanged)

- [ ] **Step 1: Grep for leftovers.** `grep -rn "buildWorkspaceTree\|WorkspaceGrouping\|workspaceRowsOf" src/` — only `runtime-rows.ts`'s own row derivation for `/runtimes` consumers may remain, and it must no longer be used for grouping.

- [ ] **Step 2: Full verification.** `dotnet build`, `dotnet test`, `pnpm lint`, `pnpm build` (dashboard). Report the actual output; do not claim green without it.

- [ ] **Step 3: Commit.** `docs: record the coordinator-owned workspace tree`

---

## Self-Review

- **Spec coverage:** design doc §2 (single owner) → Tasks 1–2; §3 (endpoint contract) → Task 2; §4 (dashboard) → Task 3; §5 (desktop) → Tasks 4–5; §6 (deletions) → Tasks 3.2, 4, 5.4; §7 (verification) → Task 6.
- **Deviation:** the design's `group` parameter and per-mode client caches are replaced by one response carrying all three arrangements. Cheaper in client code, one request per filter change, no cache invalidation on toggle. Recorded in the design doc in Task 6.
- **Type consistency:** `WorkspaceTreeRow`/`WorkspaceTreeNode` (Core) → `WorkspaceTreeRowDto`/`WorkspaceTreeNodeDto` (Backend, Desktop) → `WorkspaceRow`/`WorkspaceTreeNode` (TS). Same field names on the wire; `root` is nullable everywhere the entry may not stand for a workspace.
- **Out of scope:** folding `WorkspaceAggregator` into the endpoint (the counters and process metrics it computes are desktop-only and much larger than this change); `status`-based *sorting*; caching the response server-side.
