# Unified Docs — Markdown content, SQLite metadata

- **Date:** 2026-11-24, revised 2026-11-25 (SQLite-only metadata)
- **Status:** approved 2026-11-25; revision approved before implementation
- **Supersedes:** the managed/external split, the managed-docs offer policy, `doc_promote`, `409 doc_read_only`, and the earlier requirement for a committed per-workspace `docs-metadata.json`.

## Intent and why

Every Markdown file is a doc. A static spec under `docs/` is first-class without being copied into `.eling/docs/`. Ownership and origin were removed first; now the second storage layer is removed too. The user decided that one global SQLite index is enough — a separate JSON metadata file added complexity without being worth it.

The world after this revision: `.md` files hold content only; `DATA/eling/docs.db` holds identity, metadata, relations, annotations, and FTS; `DATA/eling/docs-audit.db` holds audit history. There is no `docs-metadata.json` anywhere.

## Decisions

- **User:** remove the managed/external distinction; everything is docs.
- **User:** `DocId` is an opaque ULID like `MemoryId`, anti-duplicate by construction.
- **User:** the dashboard reads all workspaces through one shared workspace selector and one endpoint.
- **User:** server-side `sort`/`order`; client never re-sorts.
- **User:** the watcher is owned by the dashboard-owner process, not MCP stdio processes.
- **User (2026-11-25):** drop `docs-metadata.json`; SQLite docs index alone is sufficient.
- **Agent call:** with JSON gone, `docs.db` is the **canonical local store**, not a disposable cache. Deleting it means a rescan rebuilds from Markdown with fresh ULIDs, losing status/annotation/relation edits made through tools. This is the cost of the simpler model.
- **Agent call:** ULIDs are reused by looking up `(workspace_id, path)` in `docs`, so ordinary rescans keep ids stable.
- **Agent call:** coverage policy moves into a `doc_policy` table in SQLite; `doc_scope_policy` keeps working without a config file.

## Goals / non-goals

Goals: one doc universe across workspaces; no JSON sidecar; stable ULIDs across rescans; server-side sort and paging; watcher detects manual `.md` edits; audit survives document deletion.

Non-goals: committed metadata travelling with git; cross-workspace access control; per-workspace ranking profiles; rewriting existing Markdown files; merging with Memory/Skill/Snippet subsystems.

## Design

### 1. Identity

`DocId` is `readonly record struct` over a lowercase ULID (`DocId.NewId()`, `DocId.Parse`, `DocId.TryParse`). The dedupe key is `(workspaceId, path)`: `workspaceId` is SHA1 of the canonical workspace root; `path` is the normalized relative path. On scan, the index row for that pair supplies the existing ULID; a new pair gets a new ULID. Two workspaces with the same relative path get different ULIDs.

### 2. SQLite as the store

`DATA/eling/docs.db` is one global database for all registered workspaces. Tables:

- `docs` — `id`, `workspace_id`, `path`, `kind`, `title`, `status`, `topic`, `read_order`, `archived`, `superseded_by`, `steps_total`, `steps_done`, `file_hash`, `mtime`, `indexed_at`.
- `doc_sections` — section rows including per-section status and annotation, linked to `docs` with cascade.
- `doc_refs` — backtick references.
- `doc_relations` — ULID edges, endpoints must exist in `docs`.
- `doc_policy` — key/value JSON for coverage.
- `docs_fts_porter`, `docs_fts_trigram` — FTS over title/heading/full body.

`doc_audit` stays in a separate persistent file, `DATA/eling/docs-audit.db`, with no FK to `docs`, so history survives deletion.

A legacy `docs` table without `workspace_id` is archived to a `*.legacy-<ulid>` file before its tables are rebuilt; `DocsMigration` exports audit rows once at startup before any new-index open.

### 3. Scan and mutations

`ScanAsync` enumerates `.md` under the workspace coverage, reuses the ULID for `(workspace_id, path)`, upserts the row, and prunes rows whose file no longer exists or whose path is no longer covered. A targeted scan (`paths` supplied) does not prune other files. Legacy YAML front-matter is imported once into the index row when the row is first created; Eling never writes it back.

`doc_save` writes the `.md` content and the index row. `doc_annotate` writes `doc_sections.annotation`. `doc_relation` writes `doc_relations` with ULID endpoints. `DELETE /api/docs/{id}` sets `archived: true` in `docs` and hides the row from lists; it never deletes the `.md`.

### 4. Coverage and tools

Coverage lives in `doc_policy` as include/exclude globs; default covers all workspace `.md` except `.git`, `node_modules`, `bin`, `obj`, `.next`, `.eling`, and `.superpowers/sdd/**`.

MCP tools: `doc_search`, `doc_read`, `doc_list`, `doc_save`, `doc_annotate`, `doc_staleness`, `doc_audit`, `doc_index`, `doc_relation`, `doc_scope_policy`. Six REST endpoints mirror them under `/api/docs`. `workspace` accepts repeated roots; `all` means no filter. `sort`/`order` are server-side with `name`/`asc` default and title tie-break. Lists return a `total` alongside the paged items.

### 5. Watcher

The dashboard owner runs one watcher per active registered workspace over `.md` files only. Each workspace has an O(1) dirty flag; the drain clears it before scanning so a mid-scan write re-arms it. The watcher re-arms from the workspace registry each tick and scans every workspace at startup. SSE broadcasts the shared `docs` topic; the dashboard refreshes its docs list. This follows `docs/watcher-standard.md`.

### 6. Dashboard

The Docs list uses the shared `WorkspaceSelection`, `WorkspaceScopePanel` (wide) and `WorkspaceSelect` (narrow), repeated `workspace` params, and a server-side sort control. No ownership/origin badges, no promote action. Missing docs show a full-panel empty state with Retry/Close.

## Implementation record

Shipped on `main` in layer-scoped commits: `c6ae27d`, `793f1f7`, `4732fab`, `0992677`, `9d4d5c9`, `1b9c57e`, `23c28cf`, `11b35f2`, `3cb4610`, `db97277`, `39ef9a5`, `0d65dd4`. A fresh full-branch review found three Critical and seven Important issues; all were fixed and verified (Core 1100/1100, Backend 1347/1347, docs vitest 14/14, `tsc --noEmit` clean). Minor items are deferred in the plan ledger.

The 2026-11-25 revision removes the JSON layer that those commits introduced. `docs-metadata.json` was deleted from the working tree. The revision shipped in `87a463c` (core: SQLite-only store), `34ccf2f` (Doc comment), and `5c0226f` (backend: Markdown-only watcher, empty-body reindex POST, memory-SSE tolerance for `docs` frames). Verification: Core 1095/1095, docs filter 25/25, backend docs filter 21/21, backend full suite 1347/1347, backend build 0 errors. The pre-commit hook needed a second attempt once because an unrelated `BufferedAuditLoggerTests` timing test flaked; the retry was green.

## Verification

- Core suite green; backend suite green apart from known timing flakes under parallel load.
- Dashboard docs vitest green; `tsc --noEmit` clean.
- Live cutover check against 4417 remains a follow-up because the dev host is shared with other sessions.
