# Unified Docs — one Markdown universe, JSON metadata, SQLite index

- **Date:** 2026-11-24
- **Status:** approved 2026-11-24 (implementation plan: `docs/superpowers/plans/2026-11-24-docs-unified.md`)
- **Supersedes:** `2026-11-20-docs-management-design.md` on the ownership/origin axes, the managed-docs offer policy, and the 2026-11-24 decision to put RCA documents in `.eling/docs/rca/`.

## Intent and why

The current docs subsystem split every Markdown file into **managed** (`.eling/docs/`, Eling-owned, writable, local-only) or **external** (`docs/`, repository-owned, read-only). A file had to be *migrated* before Eling treated it as a real document. That is the friction this change removes.

The new direction: **every Markdown file is a doc.** A static spec already committed under `docs/` is a first-class doc on sight — no copy into `.eling/docs/`, no `doc_promote`, no offer to accept. Markdown may live wherever it is useful; Eling indexes it, reads it, and (subject to coverage) writes it.

Two older decisions are deliberately reversed: ownership is no longer a classification axis, and the canonical metadata does not live inside the Markdown file. Instead:

- the `.md` file is **content only**;
- one **JSON file per workspace** is the source of truth for metadata;
- **SQLite is a derived index** of that JSON plus the scanned files, rebuildable at any time.

This matches the shape Eling already uses elsewhere: canonical text on disk, SQLite as a rebuildable search index.

## Decisions made in conversation

Attributed so the reader knows what was chosen and what was inferred:

- **User:** remove the managed/external distinction; everything is docs.
- **User:** static docs under `docs/` must be recognised as docs without migration.
- **User:** Markdown content and metadata should be separate; JSON for metadata, SQLite as the index of that metadata.
- **User:** one central JSON file, not one sidecar per document.
- **User:** the metadata JSON must be committable — repository state, not a local-only file.
- **User:** docs currently under `.eling/docs/` move into `docs/` alongside their peers; the managed directory is retired.
- **Agent call (revised 2026-11-24 after the user challenged the dashboard read path):** metadata JSON stays **per workspace**, but the SQLite index is **one global aggregate** at `DATA/eling/docs.db`, keyed by `(workspace, doc key)`. The dashboard manages docs across all workspaces, so cross-workspace reading is a first-class requirement, not a later merge. The global index is derived from every registered workspace's JSON + Markdown.
- **Agent call (stated, not agreed):** files outside the workspace root are still docs, keyed by their normalised absolute path; the default scan stays inside the workspace.

## Goals

- Any `.md` under the scanned paths is a doc with no ownership label and no migration step.
- Content stays clean Markdown; no required YAML front-matter.
- JSON is the only canonical metadata store; SQLite is disposable and reproducible from JSON + files.
- All docs use one write path, one audit path, one index.
- The FK failure that currently breaks `doc_save` is designed out rather than patched around.

## Non-goals

- Merging Docs with Memory, Skills, Snippets, or Intentions.
- Renaming or moving the existing 153 Markdown files.
- Per-workspace access control and ranking profiles (the global index already supports filtered cross-workspace search; see §10.8).
- A new UI design language for the Docs tab — only the ownership/origin affordances are removed.
- Deciding which third-party folders outside a workspace are allowed by default.

## Anti-goals

- Reintroducing a second "tier" that quietly decides whether a file is first-class. Ownership, `origin`, `promote`, and the managed-docs offer must not come back under another name.
- Writing metadata back into `.md` files as front-matter.
- Requiring Eling to create files next to a doc in order to manage it.

## Approaches considered

1. **Keep the current model, remove only the labels.** Rejected: `origin` still selects one of three SQLite databases, so the audit/doc FK mismatch remains, and `docs/` files would still need a placement decision. It keeps the machinery that caused the bug.
2. **Index-only metadata in SQLite.** Simplest, but metadata has no portable source of truth and disappears with the index. Rejected after the user asked for JSON as canonical metadata.
3. **JSON metadata + SQLite index (chosen).** One JSON file per workspace holds metadata keyed by doc path; `.md` holds content; one global SQLite database indexes all workspaces with FTS and a `workspace_id` column. Removes ownership, origin, promote, offer, and the FK problem together, keeps metadata inspectable and rebuildable, and gives the dashboard a single read surface. A per-workspace-index variant was considered and rejected: it would make every cross-workspace dashboard request open and merge N databases.

## Design

### 1. What is a doc

A doc is a discovered `.md` file under the effective coverage, regardless of folder. There is no `DocOwnership` and no `DocOrigin`. Kind (`spec`, `plan`, `doc`, `scratch`) is metadata, inferred from path/filename when the JSON has no override. Files under `.eling/memories/` are excluded: memory is a separate subsystem with its own format.

### 2. Identity

`DocId` is a ULID, shaped like `MemoryId` (`readonly record struct`, `DocId.NewId()` → `Ulid.NewUlid()`). It is opaque and globally unique; it is **not** derived from a path or hash. The user direction is that a doc id must be anti-duplicate by construction, the same way a memory id is.

The path is a separate field, and path — not the id — is the dedupe key: the per-workspace JSON is a map keyed by normalised path (`docs/superpowers/specs/foo.md` inside the workspace; `abs:` + normalised absolute path outside it). On scan, an existing entry for that path is reused with its ULID; a new file gets a new ULID. The JSON entry stores `id`, `path`, and `workspaceId`; the global index stores the same fields. Two workspaces with the same relative path therefore get two different ULIDs and never collide. If the JSON is lost, a rescan assigns new ULIDs — the same behavior as losing a memory store, and acceptable because the JSON is canonical.

Renaming a file produces a new path key, hence a new ULID; the old entry is archived rather than reused. Moving a doc between workspaces is an explicit re-creation in this change, not an id-preserving transfer.

### 3. Canonical metadata: one JSON file

Location: `<workspace>/docs-metadata.json` — a **committed** file at the workspace root, not under gitignored `.eling/`. It is the only canonical metadata store; a fresh clone already has it. Shape:

```json
{
  "version": 1,
  "coverage": { "include": [], "exclude": [] },
  "docs": {
    "docs/superpowers/specs/2026-11-19-agent-prompt-diet-design.md": {
      "id": "01j...ulid...",
      "path": "docs/superpowers/specs/2026-11-19-agent-prompt-diet-design.md",
      "workspaceId": "<sha1 of canonical workspace root>",
      "kind": "spec",
      "topic": "specs",
      "status": 1,
      "order": 0,
      "archived": false,
      "supersededBy": null,
      "relations": [{ "target": "...", "kind": "references" }],
      "annotations": { "section-1": { "status": 0, "text": "note" } }
    }
  }
}
```

Rules:

- An absent entry means "infer everything from path and content"; the JSON stays small.
- `doc_save` writes the `.md` content and upserts the JSON entry, then reindexes that doc. It never writes front-matter.
- The file is human-editable; a malformed entry is a non-fatal finding, not a blank catalog.
- Deleting a doc sets `archived: true` in its JSON entry and removes its index row; it never deletes the `.md` file. Audit history is not metadata and lives in a separate persistent store (§10.3).
- Only entries for paths inside the workspace are committed. Out-of-workspace docs (the `abs:` case, not scanned by default) keep their metadata in a local overlay under `.eling/`, so machine-specific paths never enter git.
- Because the file is tracked, `doc_save` and `doc_annotate` create ordinary git diffs. This is intentional: docs state is reviewable like any other repository change.

### 4. SQLite index (derived)

Location: `DATA/eling/docs.db` (global, one database for all registered workspaces). Every `docs` row carries a `workspace_id`; the database also holds sections, refs, relations, FTS (porter + trigram), and audit. Every row is reproducible from each workspace's JSON plus its scanned Markdown, so `doc_index` can rebuild the whole catalog by walking all registered workspaces. If the database is missing, the next scan rebuilds it; losing it loses no canonical state. The dashboard reads this one database with a workspace filter instead of opening N per-workspace files.

`doc_audit.doc_id` drops its FK to `docs(id)`. Audit is append-only history and must survive deletion of the document; with a single database, writes no longer target a different origin. Two failing tests in `tests/Eling.Core.Tests/DocsStorageTests.cs` already describe this contract (`WriteAuditAsync_WithDocIdMissingFromDocs_DoesNotThrow`, `DeleteAsync_KeepsAuditHistory`) and become green with this change.

### 5. Coverage and discovery

The coverage policy keeps only the "which paths are scanned" control and lives in the committed JSON header. Default: all `.md` under the workspace root, excluding `.git/**`, `node_modules/**`, `bin/**`, `obj/**`, `.next/**`, `.superpowers/sdd/**`, and `.eling/memories/**`. The `decision` (`ask`/`disabled`/`clear`) and the offer policy are removed: the index is a global runtime cache and the metadata is intentional repository state, so there is no local-footprint decision left to make.

### 6. Tool and API changes

- Removed: `doc_promote`, `doc_offer_managed`.
- Simplified: `doc_search`, `doc_list`, `doc_read`, `doc_annotate`, `doc_staleness`, `doc_audit`, `doc_index`, `doc_relation`, `doc_scope_policy` — drop `ownership` and `origin` parameters; keep kind/topic/status/pathPrefix and relation filters.
- `doc_save` — writes content + the target workspace's JSON metadata + a global index row tagged with that workspace; no `promote` flag.
- Add a `workspace` filter to the doc tools and `/api/docs` endpoints, matching the selector already used by Codebase/Memory. Repeated `workspace` params carry an explicit root list; no param (or `workspace=all`) means every workspace. The dashboard uses this to read every workspace through one endpoint.
- The Docs tab uses the **same workspace sidebar as Memory**: `WorkspaceScopePanel` beside the list on wide screens and `WorkspaceSelect` on narrow screens, both driven by `useWorkspaceRows` and the shared `WorkspaceSelection` model from `components/workspaces/selection.ts`. `docs-scope.ts` owns the page-specific URL binding (repeated `workspace` params, none = all), following the codebase page's `scope.ts`; the panel supports select-all and per-repository group toggles via `selectAll` / `toggleRoots` / `selectionSummary`. The workspace selection is independent of the sort state.
- REST mutations mirror this; `409 doc_read_only` disappears because there is no read-only class of doc.
- The docs list follows `docs/sorting-standard.md` from birth. Server sorts before mapping to DTOs; the client never re-sorts. Query parameters are exactly `sort` and `order` (`asc`/`desc`); `dir`/`direction` are forbidden. Allowed keys: `name` (title, default), `path`, `kind`, `status`, `order` (read order), `updated` (mtime). Default is `sort=name&order=asc`; an unknown value falls back to default rather than answering 400; missing `updated` sorts last in both directions; ties break by title A→Z. Dashboard persistence is URL → localStorage → default, with a segmented control directly under the panel header and above the list.
- Dashboard: remove ownership/origin badges and the promote action; keep kind, status, plan progress, relations, audit.

### 7. Watcher

The **dashboard-owner process** runs one watcher per active registered workspace; MCP stdio processes do not watch. Stdio processes are ephemeral, so a watcher there would miss edits made while no agent session is alive and would need a cross-process SSE relay just to reach the dashboard's in-process broadcaster. On start, the dashboard owner scans every active workspace to close the gap while it was down, and it re-arms its watcher set when the workspace registry changes. Each watcher covers its workspace's `.md` files, metadata JSON, and coverage config, so an edit made by any process outside Eling is visible without an Eling API call. A JSON change re-reads metadata and rebuilds that workspace's tagged rows in the global index; a `.md` change reindexes the changed file and removes rows for deleted files; only that workspace is reconciled, never the whole catalog. Keep the O(1) dirty-flag drain with clear-before-work ordering so a write landing mid-scan re-arms the flag and a burst coalesces into one scan. A scan failure is logged and the flag is re-armed. SSE event shape stays unchanged and carries the workspace id so the dashboard can refresh the right scope; only the dashboard-owner process runs the watcher. This is the first adopter of `docs/watcher-standard.md`.

### 8. Migration

Every `.md` becomes a doc where it already sits — including the five currently untracked specs/plans in `docs/superpowers/` and the files already in `docs/`. The exception is the old managed directory, which is retired as a matter of policy: every file under `.eling/docs/**` is **moved into `docs/**`**, preserving its relative subpath (`.eling/docs/rca/x.md` → `docs/rca/x.md`, `.eling/docs/specs/y.md` → `docs/specs/y.md`, and so on). That puts the ten current managed files beside their peers in `docs/`, including the RCA files already waiting in `docs/rca/`; nothing remains under `.eling/docs/` when migration finishes. A target that already exists is a conflict to report, never an overwrite.

**Executed 2026-11-24 (deviation from the original sequencing):** the user explicitly authorised a direct filesystem move for this one-time migration, outside the usual doc-tool rule, because `doc_promote` is broken by the FK defect. All ten files were moved with no target overwritten, and `.eling/docs/` was removed afterwards. A second managed pocket at `docs/.eling/docs/rca/` was retired the same day: three of its four RCA files moved to `docs/rca/`, and the fourth was an exact SHA-256 duplicate of a file already moved, so the duplicate was deleted rather than renamed. `docs/.eling/` was removed afterwards, as was the empty legacy pocket at `src/backend/Eling.Backend/.eling/`. This authorisation is limited to this migration and does not relax the tool-only rule for other `.eling/` state. The metadata export has **not** run yet; before the first rebuild it must still move non-inferable state (legacy front-matter declarations, section annotations, manually added relations, audit rows) out of the old indexes into the committed per-workspace JSON and the audit store (§10). Old index databases are disposable caches.

### 9. Consequences to record elsewhere

The following prior decisions are superseded and must be updated when this ships: two-axis ownership/origin classification, the managed-docs offer policy (including its `Accepted` state and fixed re-offer thresholds), RCA docs in `.eling/docs/rca/`, `doc_promote` one-way transition, `409 doc_read_only`, the standing rule that `.eling/docs/` is reachable only through Eling tools (that directory is no longer special), and the rule that all docs state stays local. Metadata is now a committed file at each workspace root, so the docs status/progress/relations travel with the repository. This spec and `docs/artifact-subsystems.md` are the living records.

### 10. Decisions on builder-review questions (agent calls)

A builder reading the design raised eight points where a wrong guess would matter. All are settled here; none is left to the implementer.

1. **Legacy YAML front-matter.** Read it on the first scan as an *import source only*: declared `status`, `supersededBy`, `spec:`, and section annotations seed the workspace JSON when no entry exists. JSON wins on conflict. Eling never writes front-matter back; Source view may still show the raw block. Rationale: no silent loss of approval/supersession state in the 153 existing files, while new docs stay content-only.
2. **Workspace identity.** `workspaceId` = SHA1 of the canonical workspace root, resolved with the existing `CanonicalProjectRoot.Resolve`, not a registry ULID. Stable across restarts; moving a root changes the `workspaceId` of its rows, but doc ULIDs stay as recorded in that workspace's JSON. Only registered, active workspaces are scanned. Reads accept `workspace=all`; mutations are restricted to the current workspace — a mutation may not write into another workspace's root in this change.
3. **Audit across rebuild.** Audit is canonical history, not part of the disposable index. It lives in `DATA/eling/docs-audit.db`, keyed by `(workspaceId, docId)`; deleting `DATA/eling/docs.db` leaves it intact. The rebuild check deletes only the index.
4. **Cutover from SQLite-only state.** Before the first rebuild, export annotations, manually added relations, and audit rows from the old indexes into the new JSON and audit store. A rebuild after cutover loses nothing.
5. **`DELETE /api/docs/{id}`.** Archives the doc (`archived: true` in JSON) and removes its index row; it never deletes the `.md`, including tracked files under `docs/`. Rationale: a catalog operation must not destroy repository content.
6. **Derived relations and annotations.** Keep the existing inference (`spec:` front-matter, `**Spec:**` marker, inline links, section metadata), but write inferred values to the index only, never to JSON. JSON holds explicit overrides; legacy declared relations are imported once under §10.1.
7. **Coverage config.** Coverage lives in the per-workspace JSON header, not SQLite, so index rebuilds preserve it. The watcher observes that key.
8. **Cross-workspace search.** In scope: one global index makes BM25 scores comparable across workspaces, so `workspace=all` search needs no merge step. Only access control and ranking profiles are deferred.

## Verification

- Core docs tests pass with ownership/origin removed; the two audit tests above are green.
- Backend docs tool/endpoint tests pass with promote/offer/read-only cases removed.
- `pnpm --filter eling-dashboard exec tsc --noEmit` passes (never `pnpm build` while dev server runs on 4427).
- Live check: `doc_index` discovers the five untracked specs/plans; `doc_save` on one of them writes `.md` + JSON entry + global index row; `doc_audit` still returns history after a doc is deleted.
- Dashboard check: `workspace=all` returns docs from two registered workspaces, each labelled with its workspace; a mutation on one workspace changes only that workspace's JSON and its tagged rows.
- Index rebuild check: delete `DATA/eling/docs.db`, rescan all workspaces, confirm the catalog is identical and `DATA/eling/docs-audit.db` is untouched.
- Migration check (verified 2026-11-24): neither `.eling/docs/` nor `docs/.eling/` exists; thirteen unique docs are present under matching `docs/` subpaths; no target was overwritten; the single SHA-256-identical duplicate was removed.

## Out of scope

- Workspace-level access control and per-workspace ranking profiles.
- Files outside the workspace as a default scan target (supported in identity, not enabled by default).
- Rewriting the 153 existing files to any new format.
- Changing Memory, Skill, Snippet, or Intention subsystems.

## Implementation record (2026-11-25)

Shipped in three layer-scoped commits on `main`: `c6ae27d` (core), `793f1f7` (core convention fixes), `4732fab` (backend + watcher), and `0992677` (dashboard). This spec and `docs/superpowers/plans/2026-11-24-docs-unified.md` were updated alongside them.

Deviations from the original design, recorded here rather than left implicit:

- `doc_index` gained an optional full-body argument so FTS searches Markdown content, not just headings. A malformed JSON metadata entry is non-fatal via per-entry parsing.
- The watcher broadcasts the shared `docs` topic as its reason, not a workspace id: the in-process broadcaster also feeds the memory SSE stream, and a workspace-path reason leaked into it. The dashboard refreshes the whole docs list.
- The dashboard's workspace sidebar uses the shared `WorkspaceSelection` model with repeated `workspace` query params, matching the codebase page's URL binding.
- The ten-plus-three managed Markdown files were moved directly with the user's explicit authorization before implementation; metadata export from legacy indexes is implemented but has not been run against the live workspace.

Verification: Core 1097/1097; Backend 1345/1345; docs vitest 14/14; `tsc --noEmit` clean. The full frontend suite has one unrelated time-bound flake (`format-helpers.test.ts` expects `0d|1d` where the clock reports `23h 59m`). A live cutover check against the 4417 dev host remains a follow-up because that host is shared with other sessions.
