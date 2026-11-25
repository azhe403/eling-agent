# Unified Docs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace managed/external docs with one model: content-only Markdown anywhere in a workspace, committed per-workspace JSON metadata, and one global SQLite index that the dashboard reads across all workspaces.

**Architecture:** Core owns the ULID `DocId`, the path-keyed metadata JSON store, the disposable global FTS index, and a separate persistent audit store. `DocsService` scans every registered workspace, writes content and JSON metadata, and indexes tagged rows. Backend tools/endpoints drop ownership, origin, promote, offer, and read-only refusal; the dashboard uses a workspace selector like Codebase/Memory. The ten files formerly in `.eling/docs/` were already moved into `docs/` on 2026-11-24; the remaining cutover is metadata export and index rebuild.

**Tech Stack:** .NET 10 / C# 14, xUnit, Microsoft.Data.Sqlite + FTS5 (porter + trigram), YamlDotNet (legacy front-matter import only), System.Text.Json, Next.js 16 / React 19, pnpm + vitest.

**Spec:** `docs/superpowers/specs/2026-11-24-docs-unified-design.md`

## Global Constraints

- All artifacts (code, comments, tests, docs, commits) are in English. Chat remains Indonesian.
- Pre-commit hook enforces one layer per commit: `src/backend/Eling.Core`, `src/backend/Eling.Backend`, `src/frontend/Eling.Dashboard` must be separate commits. Update `CHANGELOG.md` in each committed change set.
- Take `commit_lease` before the first `git add`, reclaim immediately before `git commit` (backend commits run the ~3 min test suite — use `ttl_seconds: 1800`), and `commit_release` right after. Never `--no-verify` / `SKIP_HOOK`. Never unstage another session's files.
- `DocId` is an opaque ULID (`DocId.NewId()`), never derived from path or hash. Workspace root SHA1 is `workspaceId`; normalized path is the dedupe key in JSON.
- `docs-metadata.json` is committed at each workspace root. Entries for paths outside the workspace live in a local overlay, never in git.
- `DATA/eling/docs.db` is a disposable derived index; `DATA/eling/docs-audit.db` is persistent audit history. `doc_audit` has no FK to `docs`.
- No direct file access to `.eling/memories/`. `.eling/docs/` no longer exists; do not recreate it.
- Every `catch` logs (standing rule). No silent catch, no `throw new NotImplementedException()`.
- Frontend verification uses `pnpm --filter eling-dashboard exec tsc --noEmit` and vitest. Never `pnpm build` / `next build` while the dev server runs on 4427.
- Before writing dashboard code, read the matching guide under `node_modules/next/dist/docs/`, as required by `src/frontend/Eling.Dashboard/AGENTS.md`.
- Targeted test runs first; full `dotnet test` only in the final verification step.
- Sorting follows `docs/sorting-standard.md`: `sort` + `order` (`asc`/`desc`), server sorts, client never re-sorts, default `name`/`asc`, unknown value falls back to default, ties break by title A→Z. No `dir`/`direction`.
- Watching follows `docs/watcher-standard.md`: the dashboard owner watches every active workspace, never MCP stdio processes; O(1) dirty flag per workspace, clear-before-work, scan-all on start, re-arm on registry change, SSE with workspace id.

## Review Focus

- **Same relative path in two workspaces** → two distinct ULIDs and correct `workspaceId`; a `workspace=all` query never merges them. Pin in Task 3 and Task 4.
- **Legacy YAML front-matter** with `status`, `supersededBy`, `spec:` → imported once into JSON; never written back into the `.md`; a spec already `approved` is not reported as `draft`. Pin in Task 4 and Task 5.
- **`workspace=all` list/search** → results from several workspaces, correct limit/pagination, BM25 from one index comparable across workspaces. Pin in Task 4.
- **Delete and audit** → `DELETE /api/docs/{id}` leaves the `.md` file byte-identical and audit history still readable after the row is gone. Pin in Task 5 and Task 6.
- **Malformed JSON metadata** → one bad entry is a non-fatal finding; the rest of the catalog still lists. Pin in Task 2.
- **Unknown sort key / null `updated`** → falls back to `name`/`asc` instead of 400, and a doc without `updated` is last in both directions. Pin in Task 4 and Task 8.

## File Structure

Created:
- `src/backend/Eling.Core/Docs/DocsMetadataEntry.cs` — one path-keyed metadata record.
- `src/backend/Eling.Core/Docs/DocsMetadataDocument.cs` — `version`, `coverage`, `docs` map.
- `src/backend/Eling.Core/Docs/DocsMetadataStore.cs` — load/save/get-or-create `docs-metadata.json`.
- `src/backend/Eling.Core/Docs/DocsLocalMetadataOverlay.cs` — out-of-workspace metadata under `.eling/`.
- `src/backend/Eling.Core/Docs/IDocsAuditStore.cs`, `src/backend/Eling.Core/Docs/Storage/SqliteDocsAuditStore.cs`.
- `src/backend/Eling.Core/Docs/DocsMigration.cs` — one-time export from old indexes.
- `tests/Eling.Core.Tests/DocsMetadataStoreTests.cs`, `tests/Eling.Core.Tests/DocsAuditStoreTests.cs`, `tests/Eling.Core.Tests/DocsServiceUnifiedTests.cs`.

Modified:
- `src/backend/Eling.Core/Docs/DocId.cs`, `Doc.cs`, `IDocsIndex.cs`, `DocScopeResolver.cs`, `DocScopeResolution.cs`, `CoveragePolicy.cs`, `DocsService.cs`, `Storage/SqliteDocsIndex.cs`, `DocListItem.cs`, `DocSearchResult.cs`, `DocParseResult.cs`.
- `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`, `Mcp/Tools/DocsReadTool.cs`, `Mcp/Tools/DocsWriteTool.cs`, `Endpoints/DocEndpoints.cs`, `Dtos/DocsDtoExtensions.cs`, `Dtos/DocsEnums.cs`, `Dtos/DocListItemDto.cs`, `Dtos/DocSearchResultDto.cs`, `Dtos/DocDto.cs`, `Dtos/DocReadResultDto.cs`, `Dtos/DocSaveRequest.cs`.
- `src/frontend/Eling.Dashboard/src/app/dashboard/docs/DocsList.tsx`, `DocCard.tsx`, `DocDetailPanel.tsx`, `docs-scope.ts`, and their tests.
- Tests: `DocsDomainTests.cs`, `DocsStorageTests.cs`, `DocsScopeResolverTests.cs`, `DocReadOnlyTests.cs`, `DocRelationValidationTests.cs`, `DocReadOnlyEndpointTests.cs`, `DocDetailPanel.test.tsx`, `docs-scope.test.ts`.

Deleted in the core-layer commit (Task 5): `DocOwnership.cs`, `DocOrigin.cs`, `DocReadOnlyException.cs`, `DocOfferState.cs`, `OfferState.cs`, `OfferCheckResult.cs`, `DocScopeDecision.cs`, `ScopeDecision.cs`, `DocPolicyRecord.cs`, `ExternalDocInference.cs`, `ExternalDocInferenceResult.cs`, `tests/Eling.Core.Tests/DocReadOnlyTests.cs`.
Deleted in the backend-layer commit (Task 6): `DocPromoteResponse.cs`, `DocOfferRequest.cs`.

---

### Task 1: ULID `DocId` and the unified `Doc` entity

**Files:**
- Modify: `src/backend/Eling.Core/Docs/DocId.cs`, `src/backend/Eling.Core/Docs/Doc.cs`, `tests/Eling.Core.Tests/DocsDomainTests.cs`

**Interfaces:**
- Produces: `DocId.NewId() -> DocId`, `DocId.Parse(string) -> DocId`, `DocId.Value -> string`; `Doc` constructor `(DocId id, string workspaceId, string path, DocKind kind, string title, DocStatusValue status, string topic, int order, bool archived, string? supersededBy, int stepsTotal, int stepsDone, string fileHash, DateTimeOffset mTime, DateTimeOffset? indexedAt = null, bool frontMatterError = false, string? frontMatterErrorMessage = null)` with properties `WorkspaceId`, `Path`, `Archived`, `Progress`. `IsExternal` / `IsManaged` are removed.

- [ ] **Step 1: Write failing tests in `tests/Eling.Core.Tests/DocsDomainTests.cs`**

```csharp
[Fact] public void DocId_NewId_IsUniqueAndOpaque() {
    var a = DocId.NewId(); var b = DocId.NewId();
    Assert.NotEqual(a.Value, b.Value);
    Assert.DoesNotContain(":", a.Value);
    Assert.Equal(a.Value, DocId.Parse(a.Value).Value);
}
[Fact] public void Doc_CarriesWorkspacePathAndArchivedFlag() {
    var doc = NewDoc(workspaceId: "ws-a", path: "docs/specs/a.md", archived: true);
    Assert.Equal("ws-a", doc.WorkspaceId);
    Assert.Equal("docs/specs/a.md", doc.Path);
    Assert.True(doc.Archived);
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter "FullyQualifiedName~DocsDomainTests"`
Expected: FAIL — `DocId.NewId` and the new `Doc` constructor do not exist.

- [ ] **Step 3: Implement the ULID `DocId` and reshape `Doc`**

`DocId` is a `readonly record struct` holding one lowercase ULID string, mirroring `Eling.Core.Memory.MemoryId`. Remove `Create`, `Ownership`, `Origin`, and `PathHash`. In `Doc`, replace `Ownership`/`Origin`/`RelPath` with `WorkspaceId`/`Path`, add `Archived`, keep the remaining properties and `Progress`.

- [ ] **Step 4: Run the tests again**

Run: the Step 2 command. Expected: PASS, and the rest of `DocsDomainTests` updated to the new constructor still passes.

- [ ] **Step 5: Commit**

Core layer only: `git add src/backend/Eling.Core/Docs/DocId.cs src/backend/Eling.Core/Docs/Doc.cs tests/Eling.Core.Tests/DocsDomainTests.cs CHANGELOG.md` then `git commit -m "refactor(docs): make DocId a ULID and unify Doc identity"`.

---

### Task 2: Committed per-workspace metadata JSON store

**Files:**
- Create: `src/backend/Eling.Core/Docs/DocsMetadataEntry.cs`, `DocsMetadataDocument.cs`, `DocsMetadataStore.cs`, `DocsLocalMetadataOverlay.cs`, `tests/Eling.Core.Tests/DocsMetadataStoreTests.cs`

**Interfaces:**
- Produces: `DocsMetadataEntry(DocId Id, string Path, string WorkspaceId, DocKind Kind, string Topic, int Status, int Order, bool Archived, string? SupersededBy, IReadOnlyList<DocRelationMeta> Relations, IReadOnlyDictionary<string, DocSectionAnnotation> Annotations)`; `DocRelationMeta(string TargetPath, RelationKind Kind)`; `DocSectionAnnotation(int Status, string? Text)`.
- Produces: `DocsMetadataStore.LoadAsync(string workspaceRoot, CancellationToken ct = default) -> Task<DocsMetadataDocument>`, `SaveAsync(string workspaceRoot, DocsMetadataDocument document, CancellationToken ct = default) -> Task`, `GetOrCreate(DocsMetadataDocument document, string path, string workspaceId) -> DocsMetadataEntry`.
- `DocsMetadataDocument`: `int Version`, `CoveragePolicy Coverage`, `Dictionary<string, DocsMetadataEntry> Docs` keyed by normalized path. File name `docs-metadata.json` at workspace root.

- [ ] **Step 1: Write failing tests in `tests/Eling.Core.Tests/DocsMetadataStoreTests.cs`**

```csharp
[Fact] public async Task LoadSave_RoundTripsEntriesAndCoverage()
[Fact] public async Task Load_WithOneMalformedEntry_KeepsOtherEntries()
[Fact] public async Task GetOrCreate_ReusesIdForSamePath_AndCreatesDifferentIdsForOtherWorkspaces()
[Fact] public void OutOfWorkspacePath_IsWrittenToLocalOverlay_NotToCommittedJson()
```

Assertions: a saved `DocsMetadataDocument` reloads with identical `Id`, `Path`, `WorkspaceId`, `Kind`, `Archived`; one malformed entry yields a finding and the other entry still loads; `abs:` paths are absent from `docs-metadata.json` and present in `.eling/docs-local-metadata.json`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter "FullyQualifiedName~DocsMetadataStoreTests"`
Expected: FAIL — the store types do not exist.

- [ ] **Step 3: Implement the store**

Use `System.Text.Json` with source-generated context if AOT needs it (follow `ShellJsonContext.cs` shape). Serialize only relative workspace paths into the committed file; route `abs:` paths to `DocsLocalMetadataOverlay`. A malformed entry is recorded in a non-fatal finding list, not thrown.

- [ ] **Step 4: Run the tests again**

Run: the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit**

Core layer only; message: `feat(docs): add committed workspace metadata JSON store`.

---

### Task 3: Global index schema and persistent audit store

**Files:**
- Modify: `src/backend/Eling.Core/Docs/IDocsIndex.cs`, `src/backend/Eling.Core/Docs/Storage/SqliteDocsIndex.cs`, `src/backend/Eling.Core/Docs/DocListItem.cs`, `src/backend/Eling.Core/Docs/DocSearchResult.cs`, `tests/Eling.Core.Tests/DocsStorageTests.cs`
- Create: `src/backend/Eling.Core/Docs/IDocsAuditStore.cs`, `src/backend/Eling.Core/Docs/Storage/SqliteDocsAuditStore.cs`, `tests/Eling.Core.Tests/DocsAuditStoreTests.cs`

**Interfaces:**
- Consumes: `Doc`, `DocId` from Task 1; `DocsMetadataDocument` from Task 2.
- Produces: `IDocsIndex.SearchAsync(string query, string? workspaceId = null, DocKind? kind = null, string? topic = null, string? pathPrefix = null, int limit = 10)`, `ListAsync(string? workspaceId = null, DocKind? kind = null, string? topic = null, string sort = "name", string order = "asc")`. Audit, policy, and offer methods are removed from `IDocsIndex`.
- Produces: `IDocsAuditStore.WriteAsync(DocAuditEntry entry, CancellationToken ct = default)`, `GetAsync(string? docId = null, int limit = 100, CancellationToken ct = default)`; `SqliteDocsAuditStore(string databasePath)`.

- [ ] **Step 1: Turn the two parked audit tests green and add workspace tests**

In `tests/Eling.Core.Tests/DocsStorageTests.cs` keep `WriteAuditAsync_WithDocIdMissingFromDocs_DoesNotThrow` and `DeleteAsync_KeepsAuditHistory` (currently red) but move them to `DocsAuditStoreTests.cs` against `SqliteDocsAuditStore`. Add:
`ListAsync_WithWorkspaceFilter_ReturnsOnlyThatWorkspace`, `SearchAsync_WorkspaceAll_RanksAcrossWorkspaces`, `DeleteAsync_PreservesAuditRow`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter "FullyQualifiedName~DocsStorageTests|FullyQualifiedName~DocsAuditStoreTests"`
Expected: FAIL — the new signatures and audit store do not exist.

- [ ] **Step 3: Change the index schema and signatures**

`docs` table: `id`, `workspace_id`, `path`, `kind`, `title`, `status`, `topic`, `archived`, `order`, `superseded_by`, `steps_total`, `steps_done`, `file_hash`, `mtime`, `indexed_at`, front-matter error fields. Drop `ownership` and `origin` columns and their FTS predicates. `doc_audit` is removed from this database. Add a one-time migration in `EnsureSchemaAsync` that rebuilds legacy tables (keep existing rows, default `workspace_id` from the old origin). FTS tables stay porter + trigram.

- [ ] **Step 4: Implement `SqliteDocsAuditStore`**

Table `doc_audit` with `id`, `doc_id` (no FK), `actor`, `action`, `before_json`, `after_json`, `at`; `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;`. Delete the audit methods from `SqliteDocsIndex` and `IDocsIndex`.

- [ ] **Step 5: Run the tests again**

Run: the Step 2 command. Expected: PASS, including both previously red tests.

- [ ] **Step 6: Commit**

Core layer only; message: `refactor(docs): global docs index with workspace filter and persistent audit store`.

---

### Task 4: Unified scan and read path in `DocsService`

**Files:**
- Modify: `src/backend/Eling.Core/Docs/DocsService.cs`, `DocScopeResolver.cs`, `DocScopeResolution.cs`, `CoveragePolicy.cs`, `DocParseResult.cs`, `tests/Eling.Core.Tests/DocsScopeResolverTests.cs`
- Create: `tests/Eling.Core.Tests/DocsServiceUnifiedTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces: `DocsService(IDocsIndex index, IDocsAuditStore audit, DocsMetadataStore metadata, DocScopeResolver resolver, ICodebaseIndex? codebaseIndex = null, ILogger<DocsService>? logger = null)`.
- Produces: `ScanAsync(bool full = false, IEnumerable<string>? paths = null, string? workspaceId = null, CancellationToken ct = default) -> Task<ScanResult>`; `ListAsync(string? workspaceId = null, DocKind? kind = null, string? topic = null, string sort = "name", string order = "asc", CancellationToken ct = default)`; `SearchAsync(string query, string? workspaceId = null, DocKind? kind = null, string? topic = null, int limit = 50, CancellationToken ct = default)`; `ReadAsync(DocId docId, bool includeSections = false, bool includeRefs = false, bool includeContent = false, CancellationToken ct = default)`.
- `DocScopeResolver` keeps `WorkspaceRoot` and `ResolveWorkspaceId(string root)` only; coverage comes from `DocsMetadataDocument.Coverage`.

- [ ] **Step 1: Write failing scan/read tests in `tests/Eling.Core.Tests/DocsServiceUnifiedTests.cs`**

```csharp
[Fact] public async Task Scan_IndexesMarkdownFromAllRegisteredWorkspaces()
[Fact] public async Task Scan_UsesExistingUlidForSamePathAndAssignsNewForUnseenPath()
[Fact] public async Task Scan_ImportsLegacyFrontMatterStatusOnce_AndNeverRewritesTheMarkdown()
[Fact] public async Task Search_WorkspaceAll_ReturnsDocsFromTwoWorkspacesWithoutIdCollision()
[Fact] public async Task Scan_WithMalformedMetadataEntry_StillIndexesOtherDocs()
[Fact] public async Task ListAsync_SortsServerSideByNameAscDefault_AndByOtherKeysWithOrder()
[Fact] public async Task ListAsync_UnknownSortKey_FallsBackToNameAsc_AndNullUpdatedIsLastBothDirections()
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter "FullyQualifiedName~DocsServiceUnifiedTests|FullyQualifiedName~DocsScopeResolverTests"`
Expected: FAIL — the new `DocsService` constructor and workspace parameters do not exist.

- [ ] **Step 3: Rewire the scan**

Enumerate `.md` from each registered workspace using the JSON coverage header. For each file, look up the JSON entry by normalized path: reuse its ULID, otherwise `DocId.NewId()`. Parse content with `HeadingChunker`, `PlanCheckboxCounter`, and reference extraction as today; when no JSON entry exists, seed `status`, `supersededBy`, and `spec:` relations from legacy YAML front-matter before stripping the block from indexed content. Index one tagged row into the global index. A malformed JSON entry is skipped with a logged finding.

- [ ] **Step 4: Rewire list/search/read**

Drop ownership/origin routing and the three-index lookup. `workspaceId == null` means the current workspace; the literal `all` skips the filter. Keep FTS porter first, trigram fallback. Pass `sort`/`order` to `IDocsIndex.ListAsync`, which sorts in SQL before mapping: `name` (title A→Z, default), `path`, `kind`, `status`, `order`, `updated`; unknown key → `name`/`asc`; `updated` null last in both directions; tie-break title A→Z.

- [ ] **Step 5: Run the tests again**

Run: the Step 2 command. Expected: PASS.

- [ ] **Step 6: Commit**

Core layer only; message: `refactor(docs): unified scan and cross-workspace read path`.

---

### Task 5: Write paths, archive semantics, and one-time cutover

**Files:**
- Modify: `src/backend/Eling.Core/Docs/DocsService.cs` (write methods), `tests/Eling.Core.Tests/DocRelationValidationTests.cs`
- Create: `src/backend/Eling.Core/Docs/DocsMigration.cs`
- Delete: the core ownership/offer/read-only types and `tests/Eling.Core.Tests/DocReadOnlyTests.cs` listed in File Structure.

**Interfaces:**
- Produces: `CreateDocumentAsync(string workspaceId, string path, string content, DocKind kind, string? topic = null, int status = 0, int order = 0, CancellationToken ct = default) -> Task<Doc>`; `UpdateDocumentAsync(DocId id, string content, CancellationToken ct = default) -> Task<Doc?>`; `ArchiveDocumentAsync(DocId id, string actor = "docs_service", CancellationToken ct = default) -> Task<bool>`; `AnnotateSectionAsync(DocId id, string anchor, int status, string? annotation, string actor = "docs_service", CancellationToken ct = default) -> Task<bool>`; `AddRelationAsync(DocId sourceId, DocId targetId, RelationKind kind, string actor = "docs_service", CancellationToken ct = default) -> Task`.
- Produces: `DocsMigration.ExportLegacyStateAsync(IDocsIndex legacyIndex, IDocsAuditStore legacyAudit, DocsMetadataStore metadata, IDocsAuditStore audit, string workspaceRoot, CancellationToken ct = default) -> Task`.

- [ ] **Step 1: Write failing write-path tests**

```csharp
[Fact] public async Task Create_WritesMarkdownAndJsonEntryAndIndexesOneTaggedRow()
[Fact] public async Task Update_KeepsTheSameUlidAndAppendsAudit()
[Fact] public async Task Archive_SetsArchivedInJson_RemovesIndexRow_LeavesMarkdownFileUntouched()
[Fact] public async Task Annotate_WritesIntoJson_NotIntoTheMarkdownFile()
[Fact] public async Task ExportLegacyState_PreservesAnnotationsRelationsAndAuditOnce()
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter "FullyQualifiedName~DocsServiceUnifiedTests|FullyQualifiedName~DocRelationValidationTests"`
Expected: FAIL.

- [ ] **Step 3: Implement the write methods**

Every mutation writes content only to the `.md`, writes metadata only to the committed JSON (local overlay for `abs:` paths), upserts the tagged index row, and appends to `IDocsAuditStore`. No mutation may write front-matter. `ArchiveDocumentAsync` sets `archived: true`, never deletes a file.

- [ ] **Step 4: Implement `DocsMigration`**

Read annotations, manually added relations, and audit rows from the legacy index; write them into the JSON and audit store; mark completion so it never repeats. It does not touch `.md` files — those were already moved on 2026-11-24.

- [ ] **Step 5: Run the tests again**

Run: the Step 2 command. Expected: PASS.

- [ ] **Step 6: Commit**

Core layer only; message: `feat(docs): unified write paths and legacy metadata export`.

---

### Task 6: Remove ownership, promote, offer, and read-only from backend surface

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`, `Mcp/Tools/DocsReadTool.cs`, `Mcp/Tools/DocsWriteTool.cs`, `Endpoints/DocEndpoints.cs`, `Dtos/*.cs` listed in File Structure, `tests/Eling.Backend.Tests/DocReadOnlyEndpointTests.cs`
- Delete: `DocPromoteResponse.cs` and `DocOfferRequest.cs` (core ownership types were already deleted in Task 5).

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces: MCP tools `doc_search`, `doc_read`, `doc_list`, `doc_save`, `doc_annotate`, `doc_staleness`, `doc_audit`, `doc_index`, `doc_relation`, `doc_scope_policy`; `doc_promote` and `doc_offer_managed` are gone. Every doc tool and `/api/docs` endpoint accepts repeated `workspace` roots; none (or `all`) = every workspace.

- [ ] **Step 1: Write failing backend tests**

In `DocReadOnlyEndpointTests.cs`, replace the `409 doc_read_only` tests with:
```csharp
[Fact] public async Task DocSearch_WorkspaceAll_ReturnsDocsFromBothWorkspaces()
[Fact] public async Task DocSave_WithWorkspaceFilter_WritesOnlyThatWorkspacesJson()
[Fact] public async Task DocDelete_LeavesMarkdownFileAndKeepsAudit()
```
Assert `doc_promote` and `doc_offer_managed` are absent from the tool catalog, and that no `/api/docs` write returns `doc_read_only`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~DocReadOnlyEndpointTests"`
Expected: FAIL.

- [ ] **Step 3: Update DI, tools, endpoints, and DTOs**

Register one global `IDocsIndex`, one `IDocsAuditStore`, and `DocsMetadataStore`; remove the three keyed origins and the offer/policy services. Drop ownership/origin fields from DTOs and the dashboard contract; add `WorkspaceId` and `Archived`. Remove promote/offer endpoints and the `doc_read_only` guard. `GET /api/docs` accepts `sort` and `order` and passes them to `DocsService.ListAsync`; no `dir`/`direction` alias.

- [ ] **Step 4: Run the tests again**

Run: the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit**

Backend layer only; message: `refactor(docs): remove ownership, promote, offer, and read-only from backend`.

---

### Task 7: Per-workspace watcher for edits made outside Eling

**Files:**
- Modify: `src/backend/Eling.Backend/DocsDirWatcher.cs`, `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`, `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs`
- Create: `tests/Eling.Backend.Tests/DocsDirWatcherTests.cs`

**Interfaces:**
- Consumes: `DocsService.ScanAsync(bool full, IEnumerable<string>? paths, string? workspaceId, CancellationToken ct)` from Task 4; `DocsMetadataStore` from Task 2; `MemoryChangeBroadcaster`; registered workspace roots from the coordinator.
- Produces: `DocsDirWatcher(DocsService docsService, MemoryChangeBroadcaster broadcaster, IEnumerable<string> workspaceRoots, TimeSpan? interval = null, ILogger<DocsDirWatcher>? logger = null)` and idempotent `Start()`. It arms one `FileSystemWatcher` per workspace over `<root>/docs/**/*.md`, `<root>/docs-metadata.json`, and `<root>/.eling/docs-local-metadata.json`; the old `.eling/docs/` watcher is gone. A change marks **only that workspace** dirty; the drain clears the flag before reconciling so a write landing mid-scan re-arms it and a burst coalesces. A `.md` change calls `ScanAsync(full: false, workspaceId: root)` to reindex changed and removed files into the global index; a metadata JSON change rebuilds that workspace's tagged rows. Every broadcast uses `DocsChangeTopics.Docs` and carries `workspaceId`. A scan failure is logged and the flag re-armed. Only the dashboard-owner process starts the watcher — MCP stdio processes never watch. `Start()` first calls `ScanAsync` for every active workspace to catch edits made while the dashboard owner was down, and re-arms the watcher set when the workspace registry changes. No cross-process SSE relay is needed because the dashboard owner owns the subscribers.

- [ ] **Step 1: Write failing tests in `tests/Eling.Backend.Tests/DocsDirWatcherTests.cs`**

```csharp
[Fact] public async Task ExternalMarkdownEdit_ReindexesThatWorkspaceOnly()
[Fact] public async Task MetadataJsonChange_RebuildsTaggedRowsForThatWorkspace()
[Fact] public async Task MarkdownFileDeleted_RemovesItsGlobalIndexRow()
[Fact] public void WriteDuringScanRearmsDirtyFlag_AndNextTickRescans()
[Fact] public async Task StartScansEveryActiveWorkspaceToCatchEditsWhileDashboardWasDown()
[Fact] public void WorkspaceRegistryChangeRearmsWatcherSet()
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~DocsDirWatcherTests"`
Expected: FAIL — the constructor does not accept workspace roots.

- [ ] **Step 3: Implement the per-workspace watcher**

Replace the single-workspace, two-directory watcher with a collection keyed by workspace root. Keep the existing O(1) dirty-flag drain and clear-before-work ordering; route each drain to that root only. Watch the committed metadata JSON and the local overlay in addition to `.md`. Remove all `.eling/docs/` path logic. Preserve the existing log-on-failure / re-arm behavior and the dashboard-owner-only start.

- [ ] **Step 4: Run the tests again**

Run: the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit**

Backend layer only; message: `feat(docs): watch manual Markdown edits per workspace and reindex globally`.

---

### Task 8: Dashboard reads every workspace through one selector

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/docs/DocsList.tsx`, `DocCard.tsx`, `DocDetailPanel.tsx`, `docs-scope.ts`, `docs-scope.test.ts`, `DocDetailPanel.test.tsx`

**Interfaces:**
- Consumes: `WorkspaceId`, `Archived`, and repeated `workspace` query params from Task 6; shared `WorkspaceSelection` helpers from `@/components/workspaces/selection` and `useWorkspaceRows` from `@/components/workspaces/use-workspace-rows`.
- Produces: a `WorkspaceScopePanel` sidebar beside the list on wide screens and a `WorkspaceSelect` on narrow screens, exactly like `memories/MemoriesList.tsx`. `docs-scope.ts` binds the selection to repeated `workspace` params (none = all), independent from `sort`/`order`. `GET /api/docs?workspace=<root>&sort=<key>&order=asc|desc` drives the list; docs show a workspace label, not ownership/origin badges. Sort state is persisted URL → localStorage → `name`/`asc`.

- [ ] **Step 1: Write failing component tests**

Update `docs-scope.test.ts` and `DocDetailPanel.test.tsx` to assert: repeated `workspace` params decode to a `WorkspaceSelection`, none decodes to `selectAll()`, and encode/decode round-trips; no `Managed`/`External` badge renders; an archived doc is visibly marked; detail view shows `path` and `workspace`. Add a `DocsList` test asserting a `WorkspaceScopePanel` is rendered beside the list and a `WorkspaceSelect` for narrow screens, with select-all and group toggles through `toggleRoots`. Add a sort test asserting a segmented `role="group"` control under the header with one button per key and an `↑`/`↓` direction arrow, that `?sort=order&order=desc` wins over stored state, and that the client never re-sorts the server-provided array.

- [ ] **Step 2: Run them to see them fail**

Run: `pnpm --filter eling-dashboard exec vitest run src/app/dashboard/docs`
Expected: FAIL.

- [ ] **Step 3: Update the components**

Remove ownership/origin badge code and promote action. Add `WorkspaceScopePanel` beside the list on wide screens and `WorkspaceSelect` on narrow, both fed by `useWorkspaceRows` and the shared `WorkspaceSelection` model, mirroring `MemoriesList.tsx` (select-all plus repository-group checkboxes through `toggleRoots`). Bind the selection in `docs-scope.ts` to repeated `workspace` params with none = all, pass the roots to the list request, and show workspace on cards and the detail panel. Keep the workspace selection state separate from the sort state. Add the sort segmented control (`role="group"`, `aria-label`, `aria-pressed`) directly under the panel header and above the list; keys `name`/`path`/`kind`/`status`/`order`/`updated` with one `↑`/`↓` arrow. Hydrate via `useState` lazy initializer in priority order URL → localStorage → `name`/`asc`; write the URL only when it differs from default; display the server array unchanged.

- [ ] **Step 4: Verify types and tests**

Run: `pnpm --filter eling-dashboard exec tsc --noEmit` then the Step 2 command. Expected: both PASS.

- [ ] **Step 5: Commit**

Frontend layer only; message: `feat(dashboard): show docs from every workspace with one selector and server-side sort`.

---

### Task 9: Final verification and cutover check

No commit. Run in order after Tasks 1–8:

- [ ] **Step 1: Full backend test suite**

Run: `dotnet test`
Expected: all pass; report any pre-existing flaky failure by name.

- [ ] **Step 2: Frontend type check**

Run: `pnpm --filter eling-dashboard exec tsc --noEmit`. Expected: PASS.

- [ ] **Step 3: Live cutover check against 4417**

Run `doc_index` and assert: the five untracked specs/plans and the thirteen migrated docs are listed; neither `.eling/docs/` nor `docs/.eling/` exists; a fresh `doc_save` on one moved file writes `.md` + `docs-metadata.json` + one global index row; `doc_audit` returns history after archiving that doc.

- [ ] **Step 4: Rebuild check**

Delete `DATA/eling/docs.db` (not the audit DB), rescan all workspaces, confirm the catalog is identical and audit rows are intact.

- [ ] **Step 5: Update the living spec**

Record in `docs/superpowers/specs/2026-11-24-docs-unified-design.md` the commits that shipped each task, any deviation from the design, and the verification results.

---

## Self-Review Notes

- Spec coverage: content-only Markdown (Tasks 2, 4, 5), committed metadata JSON (Task 2), global index + workspace filter (Tasks 3, 4), ULID DocId (Task 1), audit persistence (Task 3, 5), legacy import (Tasks 4, 5), per-workspace watcher (Task 7), tool/REST/UI changes (Tasks 6, 8), migration already executed for files (Task 9), consequences recorded (Task 9).
- The two failing FK tests parked in `DocsStorageTests.cs` are explicitly moved and turned green in Task 3.
- Sorting is covered end to end: signature (Task 4), SQL ordering and fallback (Task 4), query param (Task 6), segmented control and URL/storage persistence (Task 8); `dir`/`direction` never appears.
- Workspace scope mirrors Memory explicitly: shared `WorkspaceSelection`, `WorkspaceScopePanel` + `WorkspaceSelect`, `useWorkspaceRows`, page-owned URL binding in `docs-scope.ts`, and independence from sort state (Tasks 6, 8).
- Manual edits outside Eling are covered by Task 7, including mid-scan writes, deletions, and metadata-only changes, with per-workspace tagging in the global index.
- No production code is written before the design approval on 2026-11-24; execution begins only after the implementer reviews this plan and chooses an execution method.
