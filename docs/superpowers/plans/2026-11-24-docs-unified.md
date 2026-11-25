# Unified Docs Implementation Plan

> **Revision 2026-11-25 (approved):** the committed per-workspace `docs-metadata.json` layer is removed. `DATA/eling/docs.db` becomes the only metadata store; `docs-metadata.json` is deleted. The original Tasks 1–9 below describe the JSON-based implementation already shipped; Tasks R1–R5 at the end are the approved revision and supersede those parts.
>
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace managed/external docs with one model: content-only Markdown anywhere in a workspace, one global SQLite database as the only docs store, and a dashboard that reads all workspaces through one selector.

**Architecture:** Core owns the ULID `DocId`, the global SQLite docs database (identity, metadata, relations, annotations, FTS, coverage policy), and a separate persistent audit store. `DocsService` scans every registered workspace and writes only to SQLite. Backend tools/endpoints dropped ownership, origin, promote, offer, and read-only refusal. The dashboard uses the shared workspace selector with server-side sort and paging.

**Tech Stack:** .NET 10 / C# 14, xUnit, Microsoft.Data.Sqlite + FTS5 (porter + trigram), YamlDotNet (legacy front-matter import only), System.Text.Json, Next.js 16 / React 19, pnpm + vitest.

**Spec:** `docs/superpowers/specs/2026-11-24-docs-unified-design.md`

## Global Constraints

- All artifacts English; chat Indonesian.
- One commit per layer (`src/backend/Eling.Core`, `src/backend/Eling.Backend`, `src/frontend/Eling.Dashboard`); `CHANGELOG.md` updated in each change set.
- `commit_lease` before staging, reclaim before commit, `commit_release` after; never bypass hooks; never unstage another session's files.
- `DocId` is an opaque ULID; dedupe key is `(workspaceId, path)`; `workspaceId` = SHA1 of canonical root.
- `DATA/eling/docs.db` is canonical local state, not a disposable cache; `DATA/eling/docs-audit.db` holds history with no FK to `docs`.
- No `docs-metadata.json`, no local overlay, no JSON metadata in any code path.
- Every catch logs. Frontend verification uses `tsc --noEmit` and vitest, never `next build` while dev server runs on 4427.
- No worktree for this repo; execute on `main` under a commit lease.

## Review Focus

- Deleting `docs.db` rebuilds from Markdown with new ULIDs and loses tool-made metadata; expected and accepted.
- Two scans of the same `(workspace, path)` must reuse the same ULID.
- Two workspaces with the same relative path get distinct ULIDs and rows.
- Rows whose Markdown was deleted or is no longer covered must be pruned.
- Audit history survives document deletion and index rebuild.

---

## Original Tasks 1–9 (shipped)

Tasks 1–5 were executed as one atomic core commit (`c6ae27d`, `793f1f7`), Task 6/7 as `4732fab`, Task 8 as `0992677`, Task 9 verification as `9d4d5c9`. Post-review fixes landed in `1b9c57e`, `23c28cf`, `11b35f2`, `3cb4610`, `db97277`, `39ef9a5`, and `0d65dd4`. Their briefs remain in git history; the revision tasks below supersede the JSON-specific parts.

---

## Revision Tasks

### Task R1: Remove the JSON metadata layer

**Files:**
- Delete: `src/backend/Eling.Core/Docs/DocsMetadataStore.cs`, `DocsMetadataDocument.cs`, `DocsMetadataEntry.cs`, `DocRelationMeta.cs`, `DocSectionAnnotation.cs`, `MetadataLoadResult.cs`, `DocsLocalMetadataOverlay.cs`, `tests/Eling.Core.Tests/DocsMetadataStoreTests.cs`, `tests/Eling.Core.Tests/DocsMigrationTests.cs`
- Modify: `src/backend/Eling.Core/Docs/DocsService.cs`, `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`, `tests/Eling.Core.Tests/DocsServiceUnifiedTests.cs`

**Interfaces:** no JSON types remain; `DocsService` constructor becomes `(IDocsIndex index, IDocsAuditStore audit, IEnumerable<string> workspaceRoots, ILogger<DocsService>? logger = null)`. `DocsMigration.ExportLegacyStateAsync(string legacyIndexPath, IDocsAuditStore audit, CancellationToken ct = default)` exports audit only.

- [ ] **Step 1: Write the failing contract test**

`Constructor_UsesIndexAndAuditOnly` constructs `DocsService` with index, audit, roots; asserts `WorkspaceRoots`. Run `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter "FullyQualifiedName~DocsServiceUnifiedTests"`. Expected: FAIL (old constructor requires `DocsMetadataStore`).

- [ ] **Step 2: Delete JSON files and rewrite the constructor**

Remove listed files, change `DocsService` fields and DI registration. Keep audit store and audit-only migration.

- [ ] **Step 3: Run the filter** — Expected: PASS.

- [ ] **Step 4: Commit** — Core only: `refactor(docs): remove JSON metadata layer`.

### Task R2: SQLite identity and coverage policy

**Files:** `src/backend/Eling.Core/Docs/IDocsIndex.cs`, `Storage/SqliteDocsIndex.cs`, `DocsService.cs`, `tests/Eling.Core.Tests/DocsStorageTests.cs`, `tests/Eling.Core.Tests/DocsServiceUnifiedTests.cs`

**Interfaces:**
- Produces: `Task<Doc?> GetByPathAsync(string workspaceId, string path, CancellationToken ct = default)`.
- Produces: `Task<CoveragePolicy> GetPolicyAsync(CancellationToken ct = default)`, `Task SetPolicyAsync(CoveragePolicy policy, CancellationToken ct = default)`.
- `DocsService.GetCoveragePolicyAsync()` / `SetCoveragePolicyAsync(CoveragePolicy)` delegate to the index.

- [ ] **Step 1: Write failing tests** — `Index_GetByPathAsync_ReusesUlidAcrossScans`, `Policy_RoundTripsInSqlite`.
- [ ] **Step 2: Run them** — `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter "FullyQualifiedName~DocsStorageTests|FullyQualifiedName~DocsServiceUnifiedTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** — add `doc_policy(key TEXT PRIMARY KEY, value TEXT NOT NULL)`; `GetByPathAsync` filters `workspace_id` + `path`; rewrite `ScanAsync` to use it for ULID reuse and index policy for coverage.
- [ ] **Step 4: Run the filter** — Expected: PASS.
- [ ] **Step 5: Commit** — Core only: `feat(docs): store identity and coverage in SQLite`.

### Task R3: Mutations and pruning against files

**Files:** `src/backend/Eling.Core/Docs/DocsService.cs`, `tests/Eling.Core.Tests/DocsServiceUnifiedTests.cs`

**Interfaces:** `CreateDocumentAsync`, `UpdateDocumentAsync`, `ArchiveDocumentAsync`, `AnnotateSectionAsync`, `AddRelationAsync` keep signatures but persist only to SQLite.

- [ ] **Step 1: Write failing tests** — `Archive_KeepsRowWithArchivedFlag`, `Annotate_WritesSectionRow`, `Relation_SurvivesRescan`, `Scan_PrunesRowsForDeletedMarkdown`.
- [ ] **Step 2: Rewrite mutations** — status/order/archived/supersededBy in `docs`; annotations in `doc_sections.annotation`; relations in `doc_relations` with ULID endpoints; archive sets `archived=1` and keeps row; lists exclude archived in SQL; legacy front-matter seeds row on first creation only.
- [ ] **Step 3: Rewrite pruning** — collect covered paths for workspace; delete rows whose path is absent. Targeted scans do not prune.
- [ ] **Step 4: Run docs filter and full core suite** — Expected: PASS.
- [ ] **Step 5: Commit** — Core only: `fix(docs): persist mutations in SQLite and prune against files`.

### Task R4: Watcher and pending backend fix

**Files:** `src/backend/Eling.Backend/DocsDirWatcher.cs`, `Endpoints/DocEndpoints.cs`, `Mcp/McpServiceExtensions.cs`, `tests/Eling.Backend.Tests/DocsDirWatcherTests.cs`, `DocReindexEndpointTests.cs`, `MemoryApiTests.cs`, `CHANGELOG.md`

- [ ] **Step 1: Adjust watcher tests** — remove metadata-JSON event test; keep external `.md` edit, startup scan, registry re-arm; keep `Reindex_WithoutBody_ReturnsOk`.
- [ ] **Step 2: Remove JSON handling** — watcher filters `.md` only; DI no longer constructs metadata store; audit-only migration.
- [ ] **Step 3: Keep pending fixes** — `ReindexAsync(DocsService service, DocScanRequest? request = null, string? workspace = null)` and memory SSE tolerance for `data: docs`.
- [ ] **Step 4: Run backend docs filter and full backend suite** — Expected: PASS; report timing flakes by name and rerun filtered.
- [ ] **Step 5: Commit** — Backend only: `fix(docs): watch Markdown only and accept empty reindex POST`.

### Task R5: Docs and final verification

**Files:** `docs/superpowers/specs/2026-11-24-docs-unified-design.md`, this plan, `CHANGELOG.md`

- [ ] **Step 1: Confirm no JSON references** — `grep -r "docs-metadata\|DocsMetadata\|MetadataLoadResult\|DocsLocalMetadataOverlay" src docs` expects no hits outside history.
- [ ] **Step 2: Run suites** — Core, backend, docs vitest, `tsc --noEmit`; record in spec implementation record.
- [ ] **Step 3: Commit docs** — `docs: record SQLite-only docs revision`.

## Revision completion (2026-11-25)

- R1–R3 shipped together in `87a463c`; R2's SQLite policy and `(workspace_id, path)` lookup are included. Doc comment fix in `34ccf2f`.
- R4 shipped in `5c0226f` including the pending empty-body reindex fix and memory-SSE tolerance.
- R5 verification: Core 1095/1095; backend docs filter 21/21; full backend 1347/1347; `tsc --noEmit` clean; no JSON metadata references remain in `src`. `docs/` references are historical or explicit removal notes.

## Self-Review Notes

- Spec coverage: identity (R2), SQLite store (R2–R3), coverage (R2), watcher (R4), dashboard unchanged, audit preserved (R1–R3).
- Known accepted cost: deleting `docs.db` loses tool-made metadata and mints new ULIDs; recorded in the spec.
- Ruling: R1–R3 were one atomic core commit because constructor/interface/scan changes are compile-coupled — cost: one larger core commit for the reviewer.
- Ruling: the pre-commit hook's `BufferedAuditLoggerTests` flake was retried rather than bypassed — cost: one extra full hook run.
