# Scope Chain + Consensual Project Initialization — Implementation Plan

> **Status: COMPLETE — all tasks (1–9) implemented and verified (Core 101/101, Backend 146/146 incl. smoke). Commits remain user-controlled; working tree left uncommitted.**

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn Eling's scope model from "single nearest project + global" into an ordered scope chain (own → ancestors → global) with consent-gated project `.eling` initialization and per-memory provenance in tool responses.

**Architecture:** `ProjectScope.DiscoverChain` collects every `.eling` from cwd upward. `ScopedMemoryService` holds the ordered chain of per-level memory services (head = write target; empty chain = uninitialized, blocked default saves). `MemoryMerger` generalizes to N-level level-grouped merge with nearest-first ULID dedup. Two new MCP tools (`memory_project_status`, `memory_init_project`) implement the consent flow; the backend never auto-creates a project `.eling`. Read tools / recall DTOs carry flat `projectName` + `projectRoot` provenance (origin scope of each memory).

**Tech Stack:** .NET (Eling.Core, Eling.Backend), MCP (ModelContextProtocol SDK), SQLite FTS5 index, YAML markdown storage. No frontend changes.

**Spec:** `docs/superpowers/specs/2026-09-09-scope-chain-adoption-design.md` (rev 5).

## Global Constraints

- **Language:** everything (code, docs, comments, commit messages) in ENGLISH.
- **Hygiene (hard rule):** dummy/anonymized paths and names only. Example placeholders: org `acme`, parent repo `acme-platform`, intermediate `integrations`, workspace `payments`, example path `C:\work\acme\acme-platform\integrations\payments`. NEVER real project names / usernames / machine paths — not even as "forbidden examples". Grep-verify before handover.
- **Build/test (hard rule):** run unit tests PER csproj, NEVER solution-wide / chained (`;`). Always pass `--artifacts-path .bin-test` to `dotnet test`. Backend build: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj -p:ElingSkipDashboard=true`.
- **Commits (hard rule):** commits are user-controlled checkpoints. NEVER commit/push unless the user asks. Each task ends with a checkpoint step: report `git status --short` and STOP.
- **No auto-create:** no code path may `Directory.CreateDirectory` a project `.eling` directory. Project `.eling` is created ONLY by `memory_init_project` after user consent. Global/runtime dirs under the user scope are still auto-created.
- **Consent boundary:** default writes to an EXISTING ancestor scope are fine (no creation). Creating `.eling` (first-run OR nested) always requires consent → agent calls `memory_init_project`.
- **Carried decisions (from spec open questions, revisit on request):** (1) backend sees only process cwd — no host-provided workspace root this pass. (2) `.gitignore` update is conservative: create/update at the nearest git repo root only when runtime patterns are missing; no-op otherwise; failure non-fatal to scope creation.
- **Breaking (additive) tool shape change:** MCP read tools (`memory_get`, `memory_list`, `memory_search`) return scoped DTOs (add `scope`, `projectName`, `projectRoot`) instead of bare Core types. `ServerInstructions` must note the new fields.

## File Structure

- `src/backend/Eling.Core/Scope/ProjectScope.cs` — add `DiscoverChain` (head-only `Discover` kept).
- `src/backend/Eling.Core/Scope/ScopeChain.cs` (new) — small immutable model: ordered levels + global terminal info + posture helpers.
- `src/backend/Eling.Core/Memory/IMemoryMerger.cs` + `MemoryMerger.cs` — N-level level-grouped merge (old 2-source methods kept as N=1 wrappers).
- `src/backend/Eling.Core/Memory/IScopedMemoryService.cs` + `ScopedMemoryService.cs` — chain levels; empty-chain blocked save; expose `ChainRoots`, `IsInitialized`.
- `src/backend/Eling.Core/Memory/SaveResult.cs` — add `ProjectScopeNotInitializedException`.
- `src/backend/Eling.Core/Memory/ScopedMemory.cs` — unchanged (already `Scope` + `ProjectRoot`).
- `src/backend/Eling.Core/MemoryRecall/MemoryRecallHit.cs` — add `Scope`, `ProjectRoot`.
- `src/backend/Eling.Core/MemoryRecall/MemoryRecallResult.cs` — `RecentMemories` becomes `IReadOnlyList<ScopedMemory>`.
- `src/backend/Eling.Core/MemoryRecall/MemoryRecallService.cs` — propagate provenance into hits and recent items.
- `src/backend/Eling.Backend/Bootstrap/ProjectContext.cs` — chain-aware; no project-dir auto-create.
- `src/backend/Eling.Backend/Bootstrap/McpHostBuilder.cs`, `DashboardServices.cs` — build/supply chain (head = write target; uninitialized = empty chain).
- `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs` — register per-level services; ScopedMemoryService from chain.
- `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` — new consent/init + provenance paragraphs.
- `src/backend/Eling.Backend/Dtos/MemoryRecallMemory.cs` — add `projectName`, `projectRoot`.
- `src/backend/Eling.Backend/Dtos/ScopedSearchResultDto.cs` — add `ProjectName`.
- `src/backend/Eling.Backend/Dtos/SaveMemoryResponse.cs` — init-required action mapping.
- `src/backend/Eling.Backend/Dtos/ProjectStatusDto.cs` (new), `ProjectInitResultDto.cs` (new).
- `src/backend/Eling.Backend/Mcp/Tools/MemoryReadTool.cs` — return scoped DTOs.
- `src/backend/Eling.Backend/Mcp/Tools/MemoryWriteTool.cs` — map blocked-save exception to init-required response.
- `src/backend/Eling.Backend/Mcp/Tools/MemoryRecallTool.cs` — unchanged (DTO mapping updated in DTO).
- `src/backend/Eling.Backend/Mcp/Tools/MemoryProjectStatusTool.cs` (new).
- `src/backend/Eling.Backend/Mcp/Tools/MemoryInitProjectTool.cs` (new).
- Endpoints using `ScopedMemoryDto`/`ScopedSearchResultDto` — map new field.
- Tests: `tests/Eling.Core.Tests/ScopeTests.cs`, `MemoryMergerTests` (new or existing merger test file), `ScopedMemoryService` tests, `MemoryRecallService` tests; `tests/Eling.Backend.Tests/` tool + DTO + API tests. Fixture paths use `eling-dummy-*` style temp dirs only.

---

### Task 1: `ProjectScope.DiscoverChain` (Core)

**Files:**
- Modify: `src/backend/Eling.Core/Scope/ProjectScope.cs`
- Test: `tests/Eling.Core.Tests/ScopeTests.cs`

**Interfaces:**
- Consumes: existing `ProjectScope`, `ProjectScope.DataDirectoryName`, `ProjectScope.Discover` semantics.
- Produces: `public static IReadOnlyList<ProjectScope> DiscoverChain(string? startDirectory = null, string? stopAtDirectory = null)` — every ancestor dir from `start` upward containing `.eling`, nearest first, user-home excluded. `Discover` now returns `DiscoverChain(...).FirstOrDefault() ?? new ProjectScope(start)`.

- [ ] **Step 1: Write failing tests** (append to `ScopeTests.cs`, dummy temp trees only, e.g. `Path.Combine(Path.GetTempPath(), "eling-dummy-" + Guid.NewGuid().ToString("N")[..8])`). Create tree: `root/.eling`, `root/integrations/.eling`, `root/integrations/payments` (no `.eling`). Tests:
  1. `DiscoverChain_CollectsAllLevels_NearestFirst` — start = `payments` → roots == `[integrations, root]` (order matters).
  2. `DiscoverChain_StartHasOwnEling_OwnScopeIsHead` — start = `integrations` → `[integrations, root]`.
  3. `DiscoverChain_NoElingAnywhere_ReturnsEmpty` — fresh dir → empty list.
  4. `DiscoverChain_UserHomeExcluded` — stop walk at user home with `.eling` in home; expect no home entry (mirror existing `Discover` user-home test).
  5. `DiscoverChain_StopAtSeam_DoesNotWalkAbove` — start below `stopAt`; `.eling` above `stopAt` must not be collected.

- [ ] **Step 2: Run tests, expect FAIL**
  Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~DiscoverChain"`
  Expected: FAIL — `DiscoverChain` does not exist.

- [ ] **Step 3: Implement** `DiscoverChain` in `ProjectScope.cs`:

```csharp
public static IReadOnlyList<ProjectScope> DiscoverChain(
    string? startDirectory = null, string? stopAtDirectory = null)
{
    var start = string.IsNullOrWhiteSpace(startDirectory)
        ? Directory.GetCurrentDirectory()
        : Path.GetFullPath(startDirectory);
    var stopAt = string.IsNullOrWhiteSpace(stopAtDirectory)
        ? null
        : Path.GetFullPath(stopAtDirectory);

    var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var levels = new List<ProjectScope>();
    var current = new DirectoryInfo(start);

    while (current is not null)
    {
        var isUserHome = !string.IsNullOrWhiteSpace(userHome) &&
            string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                userHome.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        var candidate = Path.Combine(current.FullName, DataDirectoryName);
        if (!isUserHome && Directory.Exists(candidate))
        {
            levels.Add(new ProjectScope(current.FullName));
        }

        if (stopAt is not null &&
            string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                stopAt.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        current = current.Parent;
    }

    return levels.AsReadOnly();
}
```

  Then re-express `Discover` body as: `return DiscoverChain(startDirectory, stopAtDirectory).FirstOrDefault() ?? new ProjectScope(startDirectory);` (compute `start` via the same fallback rule; keep behavior identical — verify existing `Discover` tests still pass).

- [ ] **Step 4: Run tests, expect PASS**
  Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test`
  Expected: all `ScopeTests` (new + existing) PASS.

- [ ] **Step 5: Checkpoint** — run `git status --short`; do NOT commit (user-controlled). Report.

### Task 2: ScopeChain model + `ProjectContext` rework (no auto-create)

**Files:**
- Create: `src/backend/Eling.Core/Scope/ScopeChain.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/ProjectContext.cs`
- Test: `tests/Eling.Core.Tests/ScopeTests.cs` (+ compile fixes in `tests/Eling.Backend.Tests/` if the record shape changes call sites)

**Interfaces:**
- Consumes: `ProjectScope.DiscoverChain` (Task 1), `UserScope`.
- Produces:
  - `Eling.Core.ScopeChain` record: `string Cwd`, `IReadOnlyList<ProjectScope> Levels`; members `ProjectScope? Head`, `bool IsInitialized`, `bool HasOwnScope`, and `static ScopeChain Discover(string? cwd = null)`.
  - `ProjectContext` gains `ScopeChain Chain` + `string Posture` (`own-scope | ancestor-scope | uninitialized | user-home`) and `bool Uninitialized`; `ProjectScope` property = `Chain.Head ?? new ProjectScope(cwd)` (placeholder, never created on disk).

- [ ] **Step 1: Write failing tests** (append to `ScopeTests.cs`):
  1. `ScopeChain_HasOwnScope_True_WhenCwdHasEling` — dummy tree `root/.eling`, cwd `root` → `HasOwnScope == true`, `Head.Root == root`.
  2. `ScopeChain_AncestorOnly_HeadIsNearestAncestor` — cwd `root/integrations/payments` (no own `.eling`), `.eling` in `root/integrations` and `root` → `HasOwnScope == false`, `Head` = `integrations`.
  3. `ScopeChain_NoEling_Uninitialized` — `IsInitialized == false`, `Head == null`.
  Add a `ProjectContext` behavior test in `tests/Eling.Backend.Tests/` (filesystem fixture): after `ProjectContext.Discover()` from a temp cwd with NO `.eling` anywhere, assert `context.Chain.IsInitialized == false` AND the directory `.eling` was NOT created under cwd.

- [ ] **Step 2: Run tests, expect FAIL** — `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~ScopeChain"` and the new `ProjectContext` test (per-csproj, see Global Constraints).

- [ ] **Step 3: Implement `ScopeChain.cs`**:

```csharp
namespace Eling.Core;

public sealed record ScopeChain(string Cwd, IReadOnlyList<ProjectScope> Levels)
{
    public ProjectScope? Head => Levels.Count > 0 ? Levels[0] : null;
    public bool IsInitialized => Head is not null;
    public bool HasOwnScope =>
        Levels.Count > 0 &&
        string.Equals(Levels[0].Root.TrimEnd(Path.DirectorySeparatorChar),
            Cwd.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    public static ScopeChain Discover(string? cwd = null)
    {
        var start = string.IsNullOrWhiteSpace(cwd)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(cwd);
        return new ScopeChain(start, ProjectScope.DiscoverChain(start));
    }
}
```

- [ ] **Step 4: Rework `ProjectContext.cs`.** Add `Chain`, `Posture`, `Uninitialized` (see Interfaces). In `Discover()`: build `chain = ScopeChain.Discover()`. Rules:
  - `IsUserHome` unchanged; if user home → `EffectiveDataDir = userScope.GlobalDataDirectory`.
  - else if `chain.IsInitialized` → `EffectiveDataDir = chain.Head!.DataDirectory`.
  - else (uninitialized) → `EffectiveDataDir = Path.Combine(cwd, ProjectScope.DataDirectoryName)` as a PATH VALUE only — do NOT create it.
  - REMOVE `Directory.CreateDirectory(effectiveDataDir)` when the path is a project dir; keep creating only `userScope.RuntimeDirectory`. A project `.eling` dir must only ever be created by `memory_init_project` (Task 8).
  - Locate every consumer of `.EffectiveDataDir` / `.ProjectScope` (`grep -rn "EffectiveDataDir\|\.ProjectScope" src/backend`) and update compile + semantics: consumers that read/write project storage must tolerate the uninitialized path (no dir yet). `DashboardServices.cs`, `McpHostBuilder.cs`, `TestAppBuilder.cs` keep compiling (they consume `ProjectScope` + `UserScope`; Task 5 updates DI).

- [ ] **Step 5: Run tests, expect PASS**
  Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test` then `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test` (separately; fix only compile breaks caused by the record/ctor changes — do not change test semantics yet).

- [ ] **Step 6: Checkpoint** — `git status --short`; do NOT commit. Report.

### Task 3: N-level level-grouped merger (Core)

**Files:**
- Modify: `src/backend/Eling.Core/Memory/IMemoryMerger.cs`, `src/backend/Eling.Core/Memory/MemoryMerger.cs`, `src/backend/Eling.Core/Memory/ScopedMemory.cs` (add level carrier records)
- Test: existing merger tests + new cases (create `tests/Eling.Core.Tests/MemoryMergerChainTests.cs` if none fits)

**Interfaces:**
- Consumes: `ScopedMemory`, `ScopedSearchResult`, `MemorySearchResult`, `MemoryScopeKind`.
- Produces (new records, in `ScopedMemory.cs`):
  - `public sealed record MemoryLevel(string ProjectRoot, IReadOnlyCollection<Memory> Memories);`
  - `public sealed record SearchResultLevel(string ProjectRoot, IReadOnlyCollection<MemorySearchResult> Results);`
- Produces (new interface methods):
  - `IReadOnlyCollection<ScopedMemory> MergeLists(IReadOnlyList<MemoryLevel> levels, IReadOnlyCollection<Memory> globalMemories);`
  - `IReadOnlyCollection<ScopedSearchResult> MergeSearchResults(IReadOnlyList<SearchResultLevel> levels, IReadOnlyCollection<MemorySearchResult> globalResults);`
  Old 2-source methods stay as N=1 wrappers (lists byte-identical to today; search becomes STRICTLY level-grouped: project block always precedes global — deliberate consequence of the user-approved "level-grouped" decision, superseding the old −1000 boost interleave; update the affected old test expectations).

- [ ] **Step 1: Write failing tests** (new file or appended):
  1. `MergeLists_LevelGrouped_NearestFirst` — two levels (child root, parent root) + global; expect [child…, parent…, global…] block order; per-block input order preserved.
  2. `MergeLists_SameUlidAtTwoLevels_KeepsNearest` — same ULID in child + parent → only child occurrence present.
  3. `MergeLists_SameUlidProjectAndGlobal_KeepsProject` — dedup keeps nearer (project) copy.
  4. `MergeSearchResults_LevelGrouped` — parent hit with stronger (more negative) rank does NOT outrank child block; within a block ascending rank.
  5. `MergeSearchResults_N1_ListByteIdentical` — old 2-source list wrapper output equals today's output ordering (regression guard).

- [ ] **Step 2: Run tests, expect FAIL.**

- [ ] **Step 3: Implement.** Level-grouped merge, nearest-wins dedup by ULID:

```csharp
public IReadOnlyCollection<ScopedMemory> MergeLists(
    IReadOnlyList<MemoryLevel> levels, IReadOnlyCollection<Memory> globalMemories)
{
    var result = new List<ScopedMemory>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // ULID only
    foreach (var level in levels)
        foreach (var m in level.Memories)
            if (seen.Add(m.Id.Value))
                result.Add(new ScopedMemory(m, MemoryScopeKind.Project, level.ProjectRoot));
    foreach (var m in globalMemories)
        if (seen.Add(m.Id.Value))
            result.Add(new ScopedMemory(m, MemoryScopeKind.Global, null));
    return result.AsReadOnly();
}
```

  `MergeSearchResults` mirrors this: stamp `new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Project, level.ProjectRoot, r.MatchedVia, r.PorterScore, r.TrigramScore, r.QueryMode)`, nearest-wins dedup, then append global block (`ProjectRoot = null`). No cross-level re-sort (each level's results arrive rank-ascending from FTS5).

- [ ] **Step 4: Run tests, expect PASS.** Update any old merger test that asserted the boost-interleave behavior (project-first strict is the new contract). Run full `Eling.Core.Tests` project.
- [ ] **Step 5: Checkpoint** — `git status --short`; do NOT commit. Report.

### Task 4: `ScopedMemoryService` chain + blocked save (Core)

**Files:**
- Modify: `src/backend/Eling.Core/Memory/ScopedMemoryService.cs`, `IScopedMemoryService.cs`, `SaveResult.cs`
- Test: existing `ScopedMemoryService` tests + new cases

**Interfaces:**
- Consumes: Task 1/2/3 outputs, `IMemoryService`, `IMemoryScopePolicy`, `IMemoryMerger`.
- Produces:
  - Record `public sealed record ProjectLevel(ProjectScope Scope, IMemoryService Service);` (in `ScopedMemoryService.cs`).
  - New ctor: `ScopedMemoryService(IReadOnlyList<ProjectLevel> levels, IMemoryService globalService, IMemoryScopePolicy policy, IMemoryMerger merger, string cwd)`. Old ctor kept as N=1 wrapper (`levels = [new ProjectLevel(new ProjectScope(root ?? cwd), projectService)]`).
  - Interface additions: `IReadOnlyList<string> ChainRoots { get; }`, `bool IsInitialized { get; }`, `string Cwd { get; }`.
  - Exception in `SaveResult.cs`: `public sealed class ProjectScopeNotInitializedException(string cwd) : InvalidOperationException($"No project scope initialized at '{cwd}'. Ask the user for approval, then call memory_init_project.") { public string Cwd { get; } = cwd; }`

- [ ] **Step 1: Write failing tests:**
  1. `SaveAsync_ProjectScope_Uninitialized_Throws` — empty levels; `SaveAsync(memory, "project")` throws `ProjectScopeNotInitializedException`.
  2. `SaveAsync_Global_Uninitialized_StillWrites` — same service, `scope=global` → saved to global store.
  3. `SaveAsync_Head_IsWriteTarget` — 2 levels; default save lands in head service (root = head root).
  4. `ListAsync_Merged_UsesChain` — seed parent + child + global; merged returns child first, parent, global (Task 3 merge).
  5. `ListAsync_Project_OnlyHead` — merged contains parent memory, but `scope=project` returns only child memories.
  6. `ProjectService_Uninitialized_Throws` — `ProjectService` getter throws `ProjectScopeNotInitializedException` when levels empty.

- [ ] **Step 2: Run tests, expect FAIL.**

- [ ] **Step 3: Implement.** Store `_levels` + `_globalService`; `SaveAsync`: resolve kind; if Project and `_levels.Count == 0` throw; else head service. `ListAsync`/`SearchAsync` merged → fan out per level (level service search/list), feed Task 3 merger; `project` scope → head only (empty when uninitialized, returns `[]`); global unchanged. `ResolveService(MemoryScopeKind)` helper replaced by level-aware resolution; `ProjectRoot` property returns `_levels.Count>0 ? _levels[0].Scope.Root : null`; `ProjectService` property: `_levels.Count>0 ? _levels[0].Service : throw new ProjectScopeNotInitializedException(Cwd)`. Update all internal paths (`GetByIdAsync`, `DeleteAsync`, `UpdateAsync`, index rebuild, copy/move) to use the head/root-aware lookup: for project ops with a null root, target head; with a specific root, find the matching level (fall back to head).
- [ ] **Step 4: Run tests, expect PASS.** Update existing tests that construct the old ctor only if signatures changed (old ctor retained, so most pass unchanged). Run full `Eling.Core.Tests`.
- [ ] **Step 5: Checkpoint** — `git status --short`; do NOT commit. Report.

### Task 5: DI wiring for the chain (Backend)

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`, `Bootstrap/McpHostBuilder.cs`, `Bootstrap/DashboardServices.cs`, `Bootstrap/TestAppBuilder.cs`
- Test: compile + existing Backend tests; new assertion where an existing test asserts cwd auto-create (remove/flip it)

**Interfaces:**
- Consumes: `ProjectContext` (Task 2), `ProjectLevel`/chain ctor (Task 4).
- Produces: `ScopedMemoryService` registered from `context.Chain` per resolution (AddScoped factory), so the chain is re-discovered per request. No project `.eling` dir is ever created here.

- [ ] **Step 1: Adjust `AddElingCoreServices(ProjectScope, UserScope)` → add overload** `AddElingCoreServices(ScopeChain chain, UserScope userScope)`:
  1. Register global storage (unchanged, `userScope.GlobalDataDirectory`).
  2. Register per-level services as `AddScoped` resolved inside the `IScopedMemoryService` factory: for each `chain.Levels[i]` build `MemoryService(FileSystemMemoryStorage(level.DataDirectory), SqliteMemoryIndex(level.DataDirectory/index.db))` → `new ProjectLevel(level, service)`; uninitialized (`chain.Levels` empty) → empty level list. Non-scoped `IMemoryService`/`IIntentionStorage` registrations remain pointing at `chain.Head?.DataDirectory ?? chain.Cwd/.eling` (lazy storage; never created on disk).
  3. Construct: `new ScopedMemoryService(levels, globalService, policy, merger, chain.Cwd)`.
- [ ] **Step 2: Route callers** — `McpHostBuilder`/`DashboardServices` pass `context.Chain` + `context.UserScope`; `TestAppBuilder.CreateSelfContained` builds a temp chain (keep creating its temp `.eling` for tests, then assert behavior through services).
- [ ] **Step 3: Verify DI lifetime assumption (test):** in `tests/Eling.Backend.Tests/`, a tool test constructs `MemoryInitProjectTool` and `MemoryWriteTool` against freshly built services (chain re-discovered per construction) — this is what guarantees a same-session init→save sequence works. If MCP resolves tools per invocation, per-call discovery holds in production too; note the finding in a code comment.
- [ ] **Step 4: Build + run tests, expect PASS**
  Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj -p:ElingSkipDashboard=true`, then `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`. Fix compile breaks; flip any test that asserted cwd `.eling` auto-creation (behavior removed by design).
- [ ] **Step 5: Checkpoint** — `git status --short`; do NOT commit. Report.

### Task 6: Recall provenance in Core (`MemoryRecallHit`, recent items)

**Files:**
- Modify: `src/backend/Eling.Core/MemoryRecall/MemoryRecallHit.cs`, `MemoryRecallResult.cs`, `MemoryRecallService.cs`
- Test: recall service tests

**Interfaces:**
- Consumes: `IScopedMemoryService` chain results (Task 4).
- Produces:
  - `MemoryRecallHit` gains `MemoryScopeKind Scope`, `string? ProjectRoot` (defaults: `Project`, null).
  - `MemoryRecallResult.RecentMemories` type changes `IReadOnlyList<Memory>` → `IReadOnlyList<ScopedMemory>`.

- [ ] **Step 1: Write failing tests:**
  1. `Recall_Merged_AncestorHitCarriesAncestorRoot` — seed memory in parent scope only; recall from child (merged) → hit has `Scope == Project` and `ProjectRoot == parent root`.
  2. `Recall_Merged_OwnHitCarriesOwnRoot` — own-scope memory → root == own root.
  3. `Recall_Recent_KeepsScopedProvenance` — `RecentMemories[0].ProjectRoot` populated for project memory, null for global.
- [ ] **Step 2: Run tests, expect FAIL.**
- [ ] **Step 3: Implement.** In `MemoryRecallService.RecallAsync`:
  - Topic hits: `hits` are `ScopedSearchResult` (already carry per-level root from Task 4). When building each `MemoryRecallHit`, carry `hit.Scope` and `hit.ProjectRoot` (resolve `GetByIdAsync` via the same reference; the dedup `seen` set already keeps the nearest occurrence because merged search is nearest-first).
  - Recent: build `List<ScopedMemory>` from `_scoped.ListAsync(...)` instead of stripping to `Memory`; order by `UpdatedAt` desc; `Take(recentLimit)`.
  - Stats unchanged (counts).
- [ ] **Step 4: Run tests, expect PASS.** Update existing recall tests that assert the old bare shapes only where the type changed. Run full `Eling.Core.Tests`.
- [ ] **Step 5: Checkpoint** — `git status --short`; do NOT commit. Report.

### Task 7: Provenance in DTOs + read-tool response shapes (Backend)

**Files:**
- Modify: `Dtos/MemoryRecallMemory.cs`, `Dtos/ScopedSearchResultDto.cs`, `Dtos/SaveMemoryResponse.cs`, `Dtos/ScopedMemoryDto.cs` (no change needed — verify), `Mcp/Tools/MemoryReadTool.cs`, `Mcp/Tools/MemoryWriteTool.cs`, endpoint DTO mapping using `ScopedSearchResultDto`/`ScopedMemoryDto` (`Endpoints/ScopedMemoryEndpoints.cs`, `Endpoints/MemoryEndpoints.cs`)
- Test: `tests/Eling.Backend.Tests/MemoryToolsTests.cs`, `MemoryApiTests.cs`

**Interfaces:**
- Consumes: provenance-bearing Core results (Tasks 4/6).
- Produces:
  - `MemoryRecallMemory` gains `[JsonPropertyName("projectName")] string? ProjectName` and `[JsonPropertyName("projectRoot")] string? ProjectRoot`. Helper: `private static string? NameOf(string? root) => root is null ? null : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));`
  - All three `From` overloads populate `Scope/ProjectName/ProjectRoot` from the scoped/hit source; global → name/root null. `From(Memory)` (no provenance) keeps `Scope="project"`, name/root null.
  - `ScopedSearchResultDto` becomes `(string Id, double Rank, string Scope, string? ProjectName, string? ProjectRoot)`.
  - `SaveMemoryResponse` gains optional `initRequired` + `message` fields, and static `FromInitRequired(string cwd)` producing `Action="init-required"`, `Scope="project"`.

- [ ] **Step 1: Write failing tests:**
  1. `RecallDto_ProjectMemory_CarriesNameAndRoot` / `_Global_NullNull`.
  2. `SearchResultDto_HasProjectName`.
  3. `MemoryWriteTool_Save_Uninitialized_ReturnsInitRequired` — tool over empty-chain service returns `Action == "init-required"`, no file created under cwd.
- [ ] **Step 2: Run tests, expect FAIL.**
- [ ] **Step 3: Implement DTO field changes** (see Interfaces) + update endpoint mapping call sites to the new `ScopedSearchResultDto` ctor arity.
- [ ] **Step 4: Change `MemoryReadTool` return types to scoped DTOs** (breaking-additive per Global Constraints):
  - `memory_get` → `Task<ScopedMemoryDto?>`; merged branch resolves the actual scope and maps with `ScopedMemoryDto.From(memory, scope, root)`; single-scope branch likewise. `ScopedMemoryDto.From` already derives nested `project { name, root }`.
  - `memory_list` → `Task<IReadOnlyCollection<ScopedMemoryDto>>` mapping each `ScopedMemory` with its own `ProjectRoot`.
  - `memory_search` → `Task<IReadOnlyCollection<ScopedSearchResultDto>>` mapping each `ScopedSearchResult` (scope + name + root).
  - Update `MemoryWriteTool.SaveAsync`: catch `ProjectScopeNotInitializedException` → `return SaveMemoryResponse.FromInitRequired(cwd)`; keep the notifier/index behavior untouched on success.
- [ ] **Step 5: Run tests, expect PASS.** Update `MemoryToolsTests`/`MemoryApiTests` expectations to the new shapes (fields are additive on recall/list/get JSON; search adds `projectName`). Run full `Eling.Backend.Tests`.
- [ ] **Step 6: Checkpoint** — `git status --short`; do NOT commit. Report.

### Task 8: `memory_project_status` + `memory_init_project` tools (Backend)

**Files:**
- Create: `Dtos/ProjectStatusDto.cs`, `Dtos/ProjectInitResultDto.cs`, `Mcp/Tools/MemoryProjectStatusTool.cs`, `Mcp/Tools/MemoryInitProjectTool.cs`
- Modify: `Mcp/ServerInstructions.cs`
- Test: `tests/Eling.Backend.Tests/MemoryProjectToolsTests.cs` (new)

**Interfaces:**
- Consumes: `ScopeChain`/`ProjectContext` posture (Task 2), `MemoryScopeKind`.
- Produces:
  - `ProjectStatusDto(string Cwd, bool IsUserHome, bool Initialized, bool HasOwnScope, string? HeadRoot, bool Adoptable, IReadOnlyCollection<string> AncestorScopes, string Posture)`.
  - `ProjectInitResultDto(string Status, string? HeadRoot, IReadOnlyCollection<string> Chain)` — `Status` ∈ `created | already-initialized | rejected-user-home`.
  - Tool class pattern mirrors existing tools: `[McpServerToolType]`, ctor-injected chain resolver.

- [ ] **Step 1: Write failing tests:**
  1. `Status_Uninitialized_AdoptableTrue` (fresh temp cwd, no `.eling` anywhere) — `posture == "uninitialized"`, `adoptable == true`, `ancestorScopes` empty.
  2. `Status_Nested_AdoptableTrue_WithAncestors` — `.eling` only in parent → `posture == "ancestor-scope"`, `headRoot` = parent, ancestors = [parent].
  3. `Status_OwnScope_AdoptableFalse`.
  4. `Init_CreatesDotElingAndMemories` — after init, `cwd/.eling/memories/` exists; result `Status == "created"`, chain head == cwd.
  5. `Init_Idempotent` — second call → `already-initialized`, no throw.
  6. `Init_UserHome_Rejected` — cwd == user home → `rejected-user-home`; nothing created.
  7. `Init_WritesGitignoreWhenMissingPatterns` — temp git repo root == cwd with existing `.gitignore` without runtime patterns → patterns appended (`.eling/index.db*`, `.eling/*.db-journal`, `.eling/*.db-wal`, `.eling/runtime/`), `.eling/memories/` NOT ignored.
  8. `Init_GitignoreNoopWhenPatternsPresent` — unchanged file.

- [ ] **Step 2: Run tests, expect FAIL** (per-csproj).
- [ ] **Step 3: Implement `MemoryProjectStatusTool`.** Resolve `ScopeChain.Discover()` fresh per call; compute fields from the chain + user-home check (`Environment.GetFolderPath(SpecialFolder.UserProfile)`). `Adoptable = !IsUserHome && !chain.HasOwnScope`. `AncestorScopes` = `chain.Levels.Skip(chain.HasOwnScope ? 1 : 0).Select(l => l.Root)`.
- [ ] **Step 4: Implement `MemoryInitProjectTool` (`Name = "memory_init_project"`).**
  1. Re-resolve chain; if `chain.HasOwnScope` → return `already-initialized`.
  2. If cwd == user home → return `rejected-user-home` (nothing created).
  3. Else create `Directory.CreateDirectory(Path.Combine(cwd, ".eling", "memories"))`.
  4. `.gitignore` (conservative rule): walk up from cwd for nearest `.git`; if found, read `<repoRoot>/.gitignore`; if the four runtime patterns are not all present, append them; write back only on change. Wrap in try/catch — on failure set a `gitignoreWarning` field instead of throwing (scope creation still succeeds). If no `.git` found, skip silently.
  5. Return `created` with the fresh chain (head == cwd).
- [ ] **Step 5: `ServerInstructions`** — append two new sections: (a) "Project scope initialization requires user consent: when `memory_project_status` reports `adoptable`, or a default `memory_save` returns `init-required`, ask the user; on approval call `memory_init_project`. The backend never creates `.eling` on its own." (b) "Tool responses carry provenance: `projectName`/`projectRoot` identify the scope level a memory lives in (own, an ancestor, or null for global); `memory_get`/`list`/`search` return scoped payloads."
- [ ] **Step 6: Run tests, expect PASS.** Run full `Eling.Backend.Tests`.
- [ ] **Step 7: Checkpoint** — `git status --short`; do NOT commit. Report.

### Task 9: Integration validation + regression sweep

**Files:** none new; verify touched areas end-to-end.
**Test:** full both csproj suites + smoke checks.

- [ ] **Step 1: Full Core suite** — `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test` → PASS.
- [ ] **Step 2: Full Backend suite** — `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test` → PASS.
- [ ] **Step 3: Backend build (fast)** — `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj -p:ElingSkipDashboard=true` → success, no warnings introduced for the touched files.
- [ ] **Step 4: Smoke scenario (fixture, dummy paths)** — in a temp tree `root/.eling` + `root/integrations/payments` (no own `.eling`):
  1. `memory_project_status` in `payments` → `posture: ancestor-scope`, `adoptable: true`.
  2. `memory_save` default → lands in `root/.eling` (ancestor), no consent needed (nothing created).
  3. `memory_init_project` → `.eling` created under `payments`; status now `own-scope`.
  4. `memory_save` default → lands in `payments/.eling`.
  5. `memory_recall` merged → contains parent-root memory with `projectName: "acme-platform"`-style ancestor name AND own memory; `scope=project` returns only own.
  6. Fresh empty dir (no `.eling` anywhere): `memory_save` → `init-required`, no directory created; after `memory_init_project` + save → works.
- [ ] **Step 5: Hygiene sweep** — grep the working tree for real project names/usernames/machine paths (patterns per Global Constraints); zero matches in new/changed files; example placeholders (acme) used consistently.
- [ ] **Step 6: Docs touch-up** — align spec §4 note if the search ordering statement (strict level-grouped supersedes the boost interleave) needs a one-line clarification; update plan status header only when complete.
- [ ] **Step 7: Final checkpoint** — `git status --short`; report summary; do NOT commit (commits are user-controlled checkpoints).

## Handoff

Plan complete. Execution options: (1) Subagent-driven (fresh agent per task + review), or (2) inline with executing-plans. Choose one; commits remain user-controlled — every task ends with a status report instead of a commit.
