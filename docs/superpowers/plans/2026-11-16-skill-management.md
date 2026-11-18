# Skill Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Eling a first-class, machine-global Skill management subsystem: canonical `SKILL.md` folders under `~/.agents/skills/{name}/`, a dedicated SQLite FTS index, 6 MCP tools + REST endpoints, recall integration, and a master-detail dashboard page.

**Architecture:** Mirror the Memory subsystem one scope down (global only): `Eling.Core.Skill` aggregate → `FileSystemSkillStorage` (canonical IO) + `SqliteSkillIndex` (dedicated `skill-index.db`) → `SkillService` (CRUD + ownership guard) → `Mcp/Tools` + `Endpoints/SkillEndpoints.cs` → recall augmentation → Next.js dashboard page. External (non-Eling) catalog folders are indexed **read-only**, with delete-to-trash and user-called `skill_adopt` as explicit exceptions; Eling mutates only `origin = 'eling'` skills. No sync, no distribution (spec non-goals).

**Tech Stack:** .NET 10, System.Text.Json + YamlDotNet frontmatter, SQLite FTS5 (porter unicode61), MCP SDK call filters (unchanged), Next.js 16 static export, vitest + testing-library, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-22-skill-management-design.md` — every task argues from this spec; read it first.

## Global Constraints

- Storage root: `~/.agents/skills/{name}/` (user profile `.agents`, machine-global; NOT the project `.eling`, NOT XDG data dir for skills themselves).
- Trash root: `~/.agents/.trash/skills/{name}/` — sibling of `skills/`, Eling is the only writer; skillshare-untouched.
- Index: `~/.local/share/eling/skill-index.db` (dedicated; sibling of `memory.db`, **not** `index.db`; machine-global, WAL + busy_timeout + foreign_keys pragmas).
- Skill name: kebab-case `^[a-z0-9]+(-[a-z0-9]+)*$`, max 128 chars, unique across the catalog (DB unique + folder-name enforcement). Name collisions with `external`-origin skills are rejected.
- Description: max 512 chars. Body: required, non-empty (markdown instructions).
- `id` ULID inside frontmatter (`id, name, description, tags, relatedMemories, relatedSnippets, source, createdAt, updatedAt`); folder name == `name`; ULID stable across renames.
- `origin` (`eling` | `external`) lives **only in the index** — never in frontmatter, paths, or responses beyond an `origin` field; external folders are never written by Eling (no ULID back-written).
- External skills: indexed for search/recall (assign fresh index id at scan), read-only forever — no adopt, ever. Mutations go through an `EnsureElingOwned(name)` guard.
- No `skill_sync`, no manifest, no target directories, no import, no LLM drafting (spec non-goals).
- Tags: lowercase, normalized (trim/lower/dedupe, hyphen kept), single words/phrases.
- File writes atomic: temp sibling folder → rename on same volume. Delete = move to trash. Names trashed block reuse until restore/purge.
- Tests: per-csproj `dotnet test` with a disposable `ELING_OUTPUT_ROOT=.bin-test-<random>`; never solution-wide; never kill the dev backends.
- Frontend: never run `pnpm build`/`next build` while dev server on 4427 — verify with `pnpm --filter eling-dashboard exec tsc --noEmit` and `pnpm --filter eling-dashboard test`.
- No real repo/skill names in code/docs (mock only); docs English; chat Indonesian OK.
- Spec/plan files must NOT be auto-committed; the user commits specs manually. Commit code normally (husky hook gates layer-per-commit and csproj counts).
- Never create a git worktree (standing machine rule); work in `C:/Projexts/AZ/eling-agent` on `main`.

## Review Focus

Inputs/conditions most likely to bite, most likely first — each pinned by a task test below:

1. **Save vs external name collision** — `skill_save` with a name that matches an `external`-origin folder must be rejected, not overwrite. → Task 6 test.
2. **Crash between folder rename/move and index update** (rename/trash/restore) — rescan must reconcile (missing/changed flags), never resurrect stale data. → Task 5 scanner tests.
3. **Malformed frontmatter / YAML in catalog** (external noise, BOM, bad dates) — rescan/parse must skip-with-warning, never abort the scan. → Task 2 + Task 5 tests.
4. **`siblingFiles[].relativePath` traversal** — `../`, absolute paths, backslash escapes rejected before any IO. → Task 2 + Task 6 tests.
5. **Rename mid-flight** — folder rename succeeds but index update fails (or vice versa); ULID preserved, path-consistent row either way; surfaced by rescan, not corruption. → Task 3 storage tests + Task 5 scanner test.

---

### Task 1: Domain model (`Skill`, `SkillId`, kebab-case validation, tag normalization)

**Files:**
- Create: `src/backend/Eling.Core/Skill/Skill.cs`, `src/backend/Eling.Core/Skill/SkillId.cs`, `src/backend/Eling.Core/Skill/SkillOrigin.cs`, `src/backend/Eling.Core/Skill/RelatedReference.cs`, `src/backend/Eling.Core/Skill/SkillSearchResult.cs`, `src/backend/Eling.Core/Skill/SkillListMetadata.cs`
- Create: `src/backend/Eling.Core/Scope/SkillCatalogPaths.cs` (static: `CatalogDirectory`, `TrashDirectory`, `IndexFilePath`, `SkillFolder(name)`; resolved from `UserProfile` + data root)
- Test: `tests/Eling.Core.Tests/SkillTests.cs`

**Interfaces:**
- Produces: `Skill` (id `SkillId`, `Name`, `Description`, `Body`, `Tags`, `RelatedMemories`, `RelatedSnippets`, `Source?`, `Origin`, `CreatedAt`, `UpdatedAt`); `Skill.ValidateName(string)`; `Skill.NormalizeTags(IEnumerable<string>)`; `SkillCatalogPaths` static class.

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
[InlineData("collect-user-input", true)]
[InlineData("Skill", false)]
[InlineData("with_underscore", false)]
[InlineData("-leading", false)]
[InlineData("trailing-", false)]
[InlineData("double--dash", false)]
[InlineData("", false)]
public void ValidateName_enforces_kebab_case(string name, bool expected) =>
    Assert.Equal(expected, Skill.ValidateName(name));

[Fact]
public void Tags_normalize_trim_lower_dedupe_hyphen_kept()
{
    var tags = Skill.NormalizeTags(new[] { " Blazor ", "BLAZOR", "forms", "a-b" });
    Assert.Equal(new[] { "blazor", "forms", "a-b" }, tags);
}

[Fact]
public void Constructor_assigns_ulid_and_timestamps()
{
    var s = new Skill("my-skill", "desc", "# body", null, null, null, null, null);
    Assert.False(string.IsNullOrWhiteSpace(s.Id.Value));
    Assert.True(s.CreatedAt <= s.UpdatedAt);
    Assert.Equal("eling", s.Origin.WireName);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests\Eling.Core.Tests\Eling.Core.Tests.csproj --filter SkillTests --artifacts-path .bin-test-skills`
Expected: FAIL (types not defined)

- [ ] **Step 3: Implement** `Skill.cs` (record-style mutable-ish aggregate mirroring `Memory.cs`; kebab validation regex `^[a-z0-9]+(-[a-z0-9]+)*$`, `MaxNameLength = 128`, `MaxDescriptionLength = 512`; ctor validates name/description/body non-empty; static `ValidateName`, `NormalizeTags` with `[\s_./\\|]+` separator regex, min length 2, lower-cased, de-duped; ULID via `MemoryId`-style wrapper `SkillId` with strict parse). `SkillCatalogPaths.cs`: `CatalogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents")`, `SkillsSubdir = "skills"`, `TrashSubdir = ".trash" + Path.DirectorySeparatorChar + "skills"`, index at `Path.Combine(ElingPaths.ResolveRuntimeDir?…)` — if no `ResolveRuntimeDir` exists, add `public static string ResolveRuntimeDir(string dataRoot)` or compute `<dataRoot>/runtime/skill-index.db` via `ElingPaths.ResolveDataDir(null, null, null)`; keep paths centralized here, never elsewhere.

- [ ] **Step 4: Run tests**

Run: same as Step 2. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Skill src/backend/Eling.Core/Scope/SkillCatalogPaths.cs tests/Eling.Core.Tests/SkillTests.cs
git commit -m "feat(skills): skill aggregate, kebab-case validation, catalog path resolution"
```

---

### Task 2: Frontmatter serialization + `ISkillStorage` / `FileSystemSkillStorage`

**Files:**
- Create: `src/backend/Eling.Core/Skill/Serialization/SkillFrontMatter.cs`, `Skill/Storage/ISkillStorage.cs`, `Skill/Storage/FileSystemSkillStorage.cs`, `Skill/Storage/InMemorySkillStorage.cs` (tests)
- Test: `tests/Eling.Core.Tests/FileSystemSkillStorageTests.cs`

**Interfaces:**
- Produces: `ISkillStorage` — `Task SaveAsync(Skill)`, `Task<Skill?> GetByNameAsync(string)`, `Task<IReadOnlyList<Skill>> ListAllAsync()`, `Task RenameAsync(string oldName, string newName)`, `Task MoveToTrashAsync(string name)`, `Task RestoreAsync(string name)`, `Task DeleteAsync(string name)` (permanent, trash only), `Task<bool> ExistsAsync(string name)`.
- Consumes: `Skill` (Task 1), `SkillCatalogPaths`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Save_then_GetByName_roundtrips_all_fields()
{
    var dir = TempDir();
    var storage = new FileSystemSkillStorage(dir);
    var skill = new Skill("my-skill", "does things", "# body\n\nsteps", ["blazor"],
        [new RelatedReference("01m2s5a8sc8v6q2eeqneq3js6c", "global", "implements")], null, null, null);
    await storage.SaveAsync(skill);
    var read = await storage.GetByNameAsync("my-skill");
    Assert.NotNull(read);
    Assert.Equal("my-skill", read!.Name);
    Assert.Equal("does things", read.Description);
    Assert.Equal(skill.Id, read.Id);
    Assert.Equal("# body\n\nsteps", read.Body.TrimEnd());
}

[Fact]
public async Task Rename_moves_folder_and_keeps_ulid()
{
    var dir = TempDir();
    var storage = new FileSystemSkillStorage(dir);
    var skill = new Skill("old-name", "d", "b", null, null, null, null, null);
    await storage.SaveAsync(skill);
    await storage.RenameAsync("old-name", "new-name");
    Assert.Directory.Exists(Path.Combine(dir, "skills", "new-name")); // path under catalog root passed in ctor
    var read = await storage.GetByNameAsync("new-name");
    Assert.Equal(skill.Id, read!.Id);
}

[Fact]
public async Task MoveToTrash_then_Restore_restores_folder() { /* move to .trash/skills/{name}, restore, assert roundtrip */ }

[Fact]
public async Task GetByName_returns_null_for_missing_folder() { /* … */ }

[Fact]
public async Task ListAll_skips_folders_without_SKILL_md() { /* write stray folder, assert not listed */ }

[Fact]
public async Task Write_is_atomic_on_parse_failure() { /* corrupt frontmatter on read → no partial file remains */ }
```

- [ ] **Step 2: Run test** — expect FAIL.
- [ ] **Step 3: Implement.** Folder = `{catalogRoot}/skills/{name}/SKILL.md`; temp = `{catalogRoot}/skills/.tmp-{name}-{guid}` then `Directory.Move`. YAML via YamlDotNet `UnderscoredNamingConvention`, `IgnoreUnmatchedProperties`; date fields via `LegacyTolerantDateTimeOffsetConverter` (reuse `Eling.Core.Memory.Serialization`). Path-traversal guard on `name` (reject separators/`..` before any IO). `SiblingFiles` (relativePath + base64) written verbatim into the folder; reject `..`/absolute/`\`-rooted relative paths (Review Focus #4). Trash: `Directory.Move(skills/{name}, .trash/skills/{name})` creating parent; `RestoreAsync` reverses; `ListAllAsync` skips missing `SKILL.md` and records skip warnings via `ILogger`.
- [ ] **Step 4: Run tests** — expect PASS.
- [ ] **Step 5: Commit** — `feat(skills): frontmatter serialization and atomic file storage with trash restore`.

---

### Task 3: `SqliteSkillIndex` — schema, FTS5, manual sync, pragmas

**Files:**
- Create: `src/backend/Eling.Core/Skill/Storage/ISkillIndex.cs`, `SqliteSkillIndex.cs`
- Test: `tests/Eling.Core.Tests/SqliteSkillIndexTests.cs`

**Interfaces:**
- Produces: `ISkillIndex` — `Task IndexAsync(Skill)`, `Task RemoveAsync(string name)`, `Task RebuildAsync(IEnumerable<Skill>)`, `Task<Skill?> GetByNameAsync(string)`, `Task<IReadOnlyCollection<SkillSearchResult>> SearchAsync(string query, string? origin = null, int limit = 10)`, `Task<IReadOnlyList<Skill>> ListAsync(bool includeTrashed, string? origin, string sort = "name")`, `Task MarkTrashedAsync(string name, bool trashed)`, `Task AdoptExternalScanResultAsync(Skill)` (internal upsert for scanner, sets `origin='external'`), `Task<bool> IsNameTakenAsync(string name)`.

Schema (verbatim from spec §5, triggers replaced by manual upsert/delete/rebuild as in `SqliteMemoryIndex`):

```sql
CREATE TABLE skills (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    description TEXT NOT NULL,
    body TEXT NOT NULL,
    body_path TEXT NOT NULL,
    origin TEXT NOT NULL DEFAULT 'eling' CHECK (origin IN ('eling','external')),
    trashed_at TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL);
CREATE TABLE skill_tags (skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE, tag TEXT NOT NULL, PRIMARY KEY (skill_id, tag));
CREATE TABLE skill_related_memories (skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE, memory_id TEXT NOT NULL, memory_scope TEXT NOT NULL, relation TEXT NOT NULL, PRIMARY KEY (skill_id, memory_id, memory_scope));
CREATE TABLE skill_related_snippets (skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE, snippet_id TEXT NOT NULL, relation TEXT NOT NULL, PRIMARY KEY (skill_id, snippet_id));
CREATE VIRTUAL TABLE skills_fts USING fts5(name, description, body, content='skills', content_rowid='rowid', tokenize='porter unicode61 remove_diacritics 1');
```

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public async Task Index_then_Search_ranks_porter_match() { /* index 3 skills; search "blazor"; expect skills ordered by relevance */ }
[Fact] public async Task Index_twice_updates_not_duplicates() { /* same id, changed body; search finds new content only */ }
[Fact] public async Task Remove_drops_from_search() { /* Index, Remove, Search => 0 hits */ }
[Fact] public async Task List_excludes_trashed_by_default() { /* MarkTrashed; List(false) empty; List(true) contains it */ }
[Fact] public async Task Rebuild_repopulates_from_files() { /* write rows directly, RebuildAsync([skill]) => searchable */ }
[Fact] public async Task External_skill_roundtrips_as_external() { /* AdoptExternalScanResultAsync; GetByName origin == "external"; never rewritten */ }
[Fact] public async Task Schema_idempotent() { /* open twice; no "table exists" error; PRAGMA journal_mode == wal */ }
```

- [ ] **Step 2: Run test** — expect FAIL.
- [ ] **Step 3: Implement.** `OpenConnectionAsync` sets `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;`; `Pooling=False`; `EnsureSchemaAsync` with `_initGate` semaphore; `IndexAsync` does upsert (insert-or-replace by id, delete+insert FTS rows, sync tag/related tables) in one transaction; `SearchAsync` uses BM25 with porter×1.0 sort, `AndToOrThreshold = 3` OR-fallback (copy `SqliteMemoryIndex` ranking); trashed rows excluded from search unless `includeTrashed`. File path default `SkillCatalogPaths.IndexFilePath` (`<dataRoot>/skill-index.db`).
- [ ] **Step 4: Run tests** — PASS.
- [ ] **Step 5: Commit** — `feat(skills): dedicated sqlite FTS index (skill-index.db)`.

---

### Task 4: `ISkillService` — CRUD + ownership guard

**Files:**
- Create: `src/backend/Eling.Core/Skill/ISkillService.cs`, `Skill/SkillService.cs`
- Test: `tests/Eling.Core.Tests/SkillServiceTests.cs`

**Interfaces:**
- Produces: `ISkillService` — `SaveAsync(SkillSaveInput)` (create or update-by-name; validates kebab + description ≤512 + non-empty body; rejects name collisions with external skills; rename via `RenameAsync` when name changes), `GetByNameAsync`, `GetByIdAsync`, `SearchAsync(query, origin, limit)`, `ListAsync(bool includeTrashed, string? origin, int page = 1, int pageSize = 20, string sort = "name") → (items, total, matching)`, `DeleteAsync` (move to trash; Eling-only), `RestoreAsync`, `RebuildIndexAsync()`, `ScanAsync()` (delegates to Task 5 scanner). `EnsureElingOwned(name)` throws `SkillReadOnlyException` for external.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public async Task Save_creates_new_skill_in_folder_and_index() { /* … */ }
[Fact] public async Task Save_update_preserves_id_and_createdAt() { /* change description; id unchanged */ }
[Fact] public async Task Save_rejects_external_name_collision() { /* seed index row origin=external for "my-skill"; SaveAsync("my-skill") throws SkillReadOnlyException */ }
[Fact] public async Task Save_rejects_kebab_invalid_name() { /* "Bad_Name" throws */ }
[Fact] public async Task Rename_renames_folder_preserves_ulid() { /* save as "a"; SaveAsync with name "b"; folder moved; index row name == "b"; id unchanged */ }
[Fact] public async Task Delete_moves_to_trash_and_marks_index() { /* Delete; GetByName null; ListAsync(includeTrashed:true) contains it */ }
[Fact] public async Task Restore_restores_from_trash() { /* Delete+Restore; GetByName returns skill; index updated */ }
[Fact] public async Task Delete_refuses_external() { /* external row; Delete throws */ }
```

- [ ] **Step 2: Run test** — FAIL.
- [ ] **Step 3: Implement.** Orchestration: validate → resolve collision (index lookup by name; if `external` → throw) → storage write → index update (or rename path: storage.Rename + index.UpdateName) → notifier `NullSkillChangeNotifier` for now. No scopes, no judge, no near-match (YAGNI — skills are created explicitly, not smart-saved). `EnsureElingOwned` checks index `origin` before every mutator.
- [ ] **Step 4: Run tests** — PASS.
- [ ] **Step 5: Commit** — `feat(skills): skill service CRUD with ownership guard`.

---

### Task 5: `ISkillCatalogScanner` — reconcile catalog ↔ index

**Files:**
- Create: `src/backend/Eling.Core/Skill/Scanning/ISkillCatalogScanner.cs`, `Skill/Scanning/SkillCatalogScanner.cs`
- Test: `tests/Eling.Core.Tests/SkillCatalogScannerTests.cs`

**Interfaces:**
- Produces: `ScanAsync() → ScanReport(discovered, external, missing, failed, renamed)`. Behavior: enumerate `{catalogRoot}/skills/*/`; parse `SKILL.md`; folders with Eling frontmatter → `origin='eling'` upsert; else → `origin='external'` (assign ULID if absent, never write back); index rows whose folder vanished → mark missing (exclude from list by default; log warning); parse failures → skip + report, never abort scan (Review Focus #3); idempotent — second scan yields zero changes.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public async Task Scan_indexes_external_without_writing_files() { /* snapshot file bytes before/after; identical; index origin external */ }
[Fact] public async Task Scan_marks_missing_folders() { /* index eling skill; delete folder; scan; List => absent; report.missing == 1 */ }
[Fact] public async Task Scan_is_idempotent() { /* scan; scan; report2.discovered == 0 && report2.missing == 0 */ }
[Fact] public async Task Scan_skips_malformed_frontmatter() { /* folder with bad YAML; report.failed == 1; others indexed; no exception */ }
[Fact] public async Task Scan_assigns_stable_ulid_to_external_across_rescans() { /* scan twice; same index id returned both times */ }
[Fact] public async Task Scan_detects_folder_rename_by_ulid() { /* eling skill renamed folder; scan => row tracks new path via id, name unchanged? — assert id-keyed upsert, not duplicate */ }
```

- [ ] **Step 2: Run test** — FAIL.
- [ ] **Step 3: Implement.** Enumerate dirs; parse via Task 2 storage; `origin` decided by presence of valid Eling frontmatter (`id` parseable ULID + required keys); upsert by `id`, new id assigned otherwise; never write to external files; missing = rows whose stored `body_path` no longer exists; return counts.
- [ ] **Step 4: Run tests** — PASS.
- [ ] **Step 5: Commit** — `feat(skills): catalog scanner with external discovery and missing detection`.

---

### Task 6: MCP tools — `skill_save`, `skill_get`, `skill_search`, `skill_list`, `skill_delete`, `skill_restore`

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/SkillTools.cs`, `Mcp/Tools/SkillReadTool.cs`, `Mcp/Tools/SkillWriteTool.cs`
- Modify: `src/backend/Eling.Backend/Tools/ToolCatalog.cs` (add `SkillsGroup` + 6 All entries + hint string in `ToolsPolicyTool.cs`), `Mcp/McpServiceExtensions.cs` (register `ISkillService`, `ISkillIndex`, `ISkillCatalogScanner` as singletons over machine-global catalog/index paths; call scanner at host start)
- Test: `tests/Eling.Backend.Tests/SkillToolsTests.cs`

**Tool contracts (all global scope):**
- `skill_save(name, description, body, tags?, relatedMemories?, relatedSnippets?, siblingFiles?[{relativePath, contentBase64}], scope?)` → typed envelope `{ id, name, path, origin }`; errors rethrown for `ToolExceptionFilter`.
- `skill_get(idOrName)` → frontmatter + body + file path + origin.
- `skill_search(query, tag?, origin?, limit=10)` → ranked hits `{ name, description, path, origin, score }`.
- `skill_list(tag?, origin?, includeTrashed?, page=1, pageSize=20, sort="name")` → `{ items, total, matching }`.
- `skill_delete(name)` → trash + `{ trashed: true }`.
- `skill_restore(name)` → restore from trash.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public async Task skill_save_creates_skill_visible_in_folder_and_index() { /* call SaveAsync; File.Exists(…/skills/x/SKILL.md) */ }
[Fact] public async Task skill_save_rejects_kebab_invalid() { /* captured IsError true */ }
[Fact] public async Task skill_save_rejects_external_collision() { /* seed external; expect IsError */ }
[Fact] public async Task skill_save_rejects_traversal_sibling_path() { /* siblingFiles relativePath "../x"; IsError */ }
[Fact] public async Task skill_get_by_name_and_by_id() { /* both */ }
[Fact] public async Task skill_search_returns_ranked_hits_with_origin_filter() { /* index 2 eling + 1 external; search blazor; origin filter */ }
[Fact] public async Task skill_delete_then_restore() { /* delete→get null; restore→get returns */ }
[Fact] public async Task skill_list_paginates_and_filters() { /* 25 skills; page 2 pageSize 10; tag filter; matching counts */ }
```

- [ ] **Step 2: Run test** — FAIL.
- [ ] **Step 3: Implement.** Same conventions as memory tools: `[McpServerToolType]`, `[Description]` on every parameter, multiple ctor overloads, DI picks host-wired singleton; `[McpServerTool(Name = "skill_save")]` etc.; notifier hook does nothing new (no SSE topic in v1). DI: `AddSingleton<ISkillIndex>(_ => new SqliteSkillIndex(SkillCatalogPaths.IndexFilePath)); AddSingleton<ISkillStorage>(new FileSystemSkillStorage(SkillCatalogPaths.CatalogDirectory)); AddSingleton<ISkillService, SkillService>(); AddSingleton<ISkillCatalogScanner, SkillCatalogScanner>()`; host startup calls `scanner.ScanAsync()` fire-and-forget-safe (log-and-continue on failure).
  Also update `Tools/ToolCatalog.cs` (`SkillsGroup = "skills"`, 6 entries) + `Tools/ToolsPolicyTool.cs` hint string.
- [ ] **Step 4: Run tests** — PASS.
- [ ] **Step 5: Commit** — `feat(skills): MCP skill tools (save/get/search/list/delete/restore)`.

---

### Task 7: REST endpoints + DTOs + dashboard rescan route

**Files:**
- Create: `src/backend/Eling.Backend/Endpoints/SkillEndpoints.cs`, `Dtos/SkillDto.cs`, `Dtos/SkillListDtos.cs`, `Dtos/SaveSkillRequest.cs`, `Dtos/UpdateSkillRequest.cs`
- Modify: `Bootstrap/DashboardRoutes.cs` (`app.MapSkillRoutes()`), `Dtos/RecallDtos`? (not here)
- Test: `tests/Eling.Backend.Tests/SkillApiTests.cs`

- [ ] **Step 1: Write the failing test** (via `TestAppBuilder.CreateSelfContained(dashboardPort: …)`)

```csharp
[Fact] public async Task Get_api_skills_returns_items_and_metadata() { /* seed; GET; envelope code=="ok"; items length; metadata.total */ }
[Fact] public async Task Post_skill_creates_and_gets_back() { /* POST body; 201; GET {id} */ }
[Fact] public async Task Put_skill_updates_description_and_rename() { /* PUT name change; folder renamed; id stable */ }
[Fact] public async Task Put_skill_external_forbidden() { /* seed external; PUT => 409/403 */ }
[Fact] public async Task Delete_skill_moves_to_trash_then_restore() { /* DELETE; GET null; POST restore; GET returns */ }
[Fact] public async Task Post_rescan_indexes_new_external_skill() { /* drop pre-made folder with foreign SKILL.md; POST /api/skills/rescan; GET list shows external */ }
```

- [ ] **Step 2: Run test** — FAIL.
- [ ] **Step 3: Implement.** `MapSkillRoutes()`: `GET /api/skills` (query/tag/origin/page/pageSize/includeTrashed/sort), `GET /api/skills/{id}`, `POST /api/skills`, `PUT /api/skills/{id}` (rename-aware), `DELETE /api/skills/{id}`, `POST /api/skills/{id}/restore`, `POST /api/skills/rescan`. DTOs mirror `MemoryListDto`/`ScopedMemoryDto` shapes (`{ items, metadata{ total, matching, limit, offset } }`). Envelope from `ApiEnvelopeMiddleware`. Error mapping: external-mutation → 409 `skill_read_only`; missing → 404 `skill_not_found`; invalid name → 400.
- [ ] **Step 4: Run tests** — PASS.
- [ ] **Step 5: Commit** — `feat(skills): REST endpoints for skills catalog`.

---

### Task 8: Recall augmentation — surface skills in `recall`

**Files:**
- Modify: `src/backend/Eling.Core/Recall/RecallResult.cs` (add `IReadOnlyList<SkillRecallHit> Skills`), `Recall/RecallHit.cs` (new record `SkillRecallHit(name, description, origin, path)`), `Recall/RecallService.cs` (after topic merge, run `ISkillService.SearchAsync(joinedTopics, ..., limit: StandingBaselineLimit)`; tolerate missing/unconfigured service — skip silently if not provided)
- Modify: `src/backend/Eling.Backend/Dtos/RecallResponse.cs` (map `Skills`), `Mcp/Tools/RecallTool.cs` (emit skills in DTO; `agentTurn` block), `src/backend/Eling.Backend/Agent/Services/AgentTurnService.cs` (append `=== AUTO-RECALLED SKILLS ===` section between Standing and Recent when hits exist; `$"- [{hit.Name}] {hit.Description} ({hit.Path})"`)
- Test: `tests/Eling.Core.Tests/RecallTests.cs` (extend), `tests/Eling.Backend.Tests/RecallToolTests.cs` (extend)
- Register DI: `ISkillService` must be resolvable in recall tests (test DI fixture provides Null/fake skill service).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public async Task Recall_includes_matching_skills_by_name_and_description() { /* seed skill "collect-user-input"; recall topic "blazor forms"; Skills contains it */ }
[Fact] public async Task Recall_does_not_inline_skill_bodies() { /* assert payload size < body length; contains path */ }
[Fact] public async Task Recall_returns_skills_even_when_memory_index_empty() { /* skills-only hit */ }
```

- [ ] **Step 2: Run test** — FAIL.
- [ ] **Step 3: Implement** as above; skip gracefully when `ISkillService` is null (memory-only harness unchanged).
- [ ] **Step 4: Run tests** — PASS.
- [ ] **Step 5: Commit** — `feat(skills): recall surfaces eling and external skills as reference-only block`.

---

### Task 9: Dashboard — `/dashboard/skills` page (card grid + right master-detail panel)

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/skills/page.tsx`, `SkillsList.tsx`, `SkillCard.tsx`, `SkillDetailPanel.tsx`, `app/dashboard/skills/skills-list.test.tsx`? (avoid real skill name in test files — use `SkillCard.test.tsx` / `skills.test.tsx` with mock names), `lib/skill-types.ts` or extend `lib/types.ts` (`Skill`, `SkillsListResponse`)
- Modify: `src/components/app-sidebar.tsx` (add `{ title: "Skills", url: "/dashboard/skills", icon: <Wrench-like icon, e.g. `Sparkles` or `GraduationCap` className="size-4"/> }` after `Tools` entry), `lib/types.ts`
- Test: colocated `*.test.tsx` with stubbed `fetch`/`EventSource` + mocked `DashboardHeader`

Spec of the page (from §7): search box (name/desc/body), tag filter chips, origin filter (`all`/`eling`/`external`), card grid (1 col / 2 col at `lg`), detail panel sticky `≈45%` (or `xl:flex` pattern from codebase page with dialog fallback below breakpoint), edit form inline (name/desc/tags/body), Delete/Restore actions, external read-only hint, header: count + `Rescan catalog` + `New Skill`.

- [ ] **Step 1: Write the failing test**

```tsx
it("renders skill cards from the envelope and filters by origin", async () => {
  stubFetchJson({ items: [mockSkill("eling"), mockSkill("external")], metadata: { total: 2, matching: 2 } })
  render(<SkillsList />)
  expect(await screen.findByText("my-skill")).toBeTruthy()
  fireEvent.click(screen.getByText("External"))
  expect(screen.queryByText("eling-skill-name")).toBeNull()
})

it("opens detail panel on card click and disables edit for external", async () => { /* click; aside renders; delete button disabled for external */ })

it("save calls PUT /api/skills/:id and returns to view mode", async () => { /* mock fetch capture */ })

it("delete for eling skill calls DELETE then restores via trash toggle", async () => { /* mock sequence */ })
```

- [ ] **Step 2: Run test** — FAIL (`pnpm --filter eling-dashboard test`).
- [ ] **Step 3: Implement** following `MemoriesList.tsx` patterns exactly: server `page.tsx` + `Suspense` + `pageTitle("Skills")`, client list with `useCallback(load)`, `cache: "no-store"`, `LoadingBar`, error banner (5s auto-dismiss), dashed-border empty state, `utcToLocal` for timestamps. `SkillCard` mirrors `MemoryCard` (no scope badges; `origin` badge + tag chips + monospace path in detail). `SkillDetailPanel` uses `xl:flex` + narrow-screen stacked layout (master-detail, no dialog — simpler than codebase page). No SSE (manual Refresh button + refetch after mutations). External skills: Edit/Delete disabled + hint text.
- [ ] **Step 4: Verify** — `pnpm --filter eling-dashboard exec tsc --noEmit` (NOT `next build`), `pnpm --filter eling-dashboard test` both green.
- [ ] **Step 5: Commit** — `feat(skills): /dashboard/skills page with master-detail card grid`.

---

### Task 10: Final verification & smoke

- [ ] **Step 1:** Run per-project test suites: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test-skills-final`, `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName!~DashboardLifecycleTests" --artifacts-path .bin-test-skills-final`, `pnpm --filter eling-dashboard test`. Expected: all green.
- [ ] **Step 2:** Grep for real-name leaks in new files (`git diff --name-only | xargs grep -i -E "skill_name_real|repo-name"`; expect zero matches) — docs/code stay mock.
- [ ] **Step 3:** Manual smoke (with dev backends up): `POST /api/skills` create via curl/HTTP file → confirm folder in `~/.agents/skills/<name>/SKILL.md` + 200 from `GET /api/skills`; `POST /api/skills/rescan`; confirm `recall` for a topic lists the skill (via MCP client or dashboard MCP panel). Then delete it to trash and restore; confirm `.trash` behavior.
- [ ] **Step 4:** Update the plan checkbox states and spec `Status:` (leave Draft for review — user commits); report delta vs spec if any step deviated (e.g. manual FTS sync instead of triggers — record in spec §5 note on merge).
- [ ] **Step 5:** Final commit(s) split per layer per husky (core / backend / frontend).

---

### Task 11: External delete-to-trash (decision 2026-11-16) — implemented

- [x] User decision 2026-11-16: the "external skills are read-only FOREVER" rule is revoked for DELETE ONLY. External skills may be moved to the catalog trash (`~/.agents/.trash/skills/{name}/`) by explicit user action — restorable, same semantics as eling-managed skills; edit/rename/overwrite of external skills remain forbidden; adoption was at that time rejected.
- [x] User decision 2026-11-16 (later the same day): `skill_adopt` reinstated — external-origin skills may be converted to Eling-managed, in place, by explicit user call (`skill_adopt` / dashboard Adopt action). Frontmatter `id`/`createdAt`/`updatedAt` are added verbatim-preserving; origin flips to `eling`; the file is not otherwise modified. PUT/rename/delete-edit remain forbidden for external.
- [x] Core: external delete allowed (index row required, folder moved untouched); restore re-activates the preserved external index row without parsing the file; trashed names stay blocked for reuse; edit/rename still refuse external.
- [x] Backend: MCP `skill_delete`/`skill_restore` descriptions updated (external delete-to-trash, restore works; rename/edit still forbidden); REST `DELETE /api/skills/{id}` allows external (`{ trashed: true }`); `POST /api/skills/{id}/restore` works for externally-trashed skills; `includeTrashed=true` lists trashed externals (already origin-agnostic).
- [x] Frontend: Delete enabled for external skills with an external-specific confirm dialog (`Delete External Skill?`, names the skill + `~/.agents/.trash/skills/{name}/` path); external hint line updated; trash view offers Restore for trashed externals; external edit remains disabled.
- [x] Spec §9 rule 2 amendment annotated in the working tree only (never committed — user commits specs manually).
