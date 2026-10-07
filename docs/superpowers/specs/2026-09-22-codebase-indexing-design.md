# Codebase Indexing via MCP — Design (mixed manual + watcher, rename index.db → memory.db)

- Date: 2026-09-22
- Status: Implemented — shipped 2026-09-29 (commits 58fb737…d5bffff, all dated 2026-09-29)
- Implementation note: the codebase DB did **not** land at `.eling/codebase.db` as
  §Goals specifies. It ships in a global per-workspace store
  (`%LOCALAPPDATA%/eling/codebase/eling-<hash>.db`, resolved by
  `ElingPaths.ResolveCodebaseDbPath`). The memory-side `index.db` → `memory.db`
  rename **did** happen as designed. Treat the §Goals line
  "Clean naming: `memory.db` for memories, `codebase.db` for the codebase" as
  superseded on the codebase half; the rest of this design is what shipped.
- Scope: Eling backend + MCP tools + storage
- Deciders: eling backend (4417), frontend proxy (4427)
- Related: `docs/recall-architecture.md`, `src/backend/Eling.Core/Memory/Storage/SqliteMemoryIndex.cs`, `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`

## 1. Context

Eling already has durable memory: markdown files under `.eling/memories/` tracked by Git as the source of truth, plus an SQLite FTS5 cache in `index.db` (Porter + trigram + BM25 with AND→OR fallback). Recall via `memory_recall` already combines topic search, recently updated memories, and outstanding intentions. The codebase files themselves (`src/backend/**/*.cs`, `src/frontend/**/*`, `docs/**/*.md`) have no index yet — agents rely on ad-hoc `file_search`/`grep`, which is slow and unranked.

The requirement: index the codebase through **MCP tools** (not CLI), in **mixed mode** — a manual trigger (`codebase_index`) plus a debounced background watcher that syncs incrementally and gently. Search results must be directly injectable into the agent prompt, reusing the existing FTS infrastructure, without adding per-language processes (LSP is phase 2).

The `index.db → memory.db` rename is folded into this spec because the timing is right: the moment `codebase.db` exists, the generic name `index.db` becomes ambiguous. `memory.db` vs `codebase.db` reads instantly.

Tenet preserved: disk is truth, SQLite is cache. Deleting `*.db` loses nothing — just rebuild.

## 2. Goals / Non-Goals

### Goals
- Agents can call MCP tools to index and search codebase snippets without opening a terminal; results arrive BM25-ranked and ready to inject.
- Mixed mode: manual at any time + a debounced incremental watcher (hash+mtime), on by default, never blocking search.
- Reuse the existing FTS engine (Porter `unicode61 remove_diacritics 1` + trigram), no new dependencies.
- Both databases are caches, 100% git-ignored, rebuildable; source of truth stays on disk.
- Clean naming: `memory.db` for memories, `codebase.db` for the codebase.
- Transparent migration for existing installs that still have `index.db`.

### Non-Goals (this phase)
- Precise per-language symbol parsing / LSP (`goto_definition`, `find_references`) — phase 2.
- Vector embeddings / semantic rerankers — YAGNI for MVP.
- Indexing large binary/generated files. Indexing itself stays per-project (one `codebase.db` per `.eling/`); cross-project search is read-only federation over explicitly opted-in sibling indexes (§12), not a global index or auto-discovery.

## 3. Architecture & Storage

### 3.1 Layout `.eling/`

```
.eling/
  memories/            # markdown, git-tracked — source of truth (unchanged)
  memory.db            # memory FTS (renamed from index.db) — cache
  memory.db-wal/shm
  codebase.db          # new — codebase FTS — cache
  codebase.db-wal/shm
  runtime/             # ignored (unchanged)
```

Why separate databases: corruption isolation (one broken does not affect the other), clean maintenance, explicit naming. The existing ` .eling/*.db` wildcard in `.gitignore` already covers both, but explicit lines `memory.db*` / `codebase.db*` are added for clarity; the legacy `index.db*` line stays during the transition.

### 3.2 Migration `index.db → memory.db`

DI does not hardcode the name in `SqliteMemoryIndex` — only in `McpServiceExtensions.cs` (5 sites: project scope, global `~/.config/eling`, chain levels around lines ~35,41,90,102,119). Change: every `Path.Combine(dataDir, "index.db")` → `Path.Combine(dataDir, "memory.db")`.

One-time migration inside `AddElingCoreServices(rootPath)`:

```
if File.Exists(memory.db) → use it
else if File.Exists(index.db) → File.Move(index.db → memory.db)
                               + Move(index.db-wal → memory.db-wal) if present
                               + Move(index.db-shm → memory.db-shm) if present
                               + log info "migrated index.db → memory.db"
else → create fresh
```

Idempotent, no heavy data migration because it is a cache. Global scope (`ELING_USER_SCOPE` / `~/.config/eling`) and every `ScopeChain` level run the same migration. Tests/docs that mention `index.db` are updated; old plan documents are not retroactively edited — only code, `.gitignore`, and new docs.

### 3.3 Tables in `codebase.db` (mirror SqliteMemoryIndex)

```sql
CREATE TABLE IF NOT EXISTS codebase_files(
  path TEXT PRIMARY KEY,
  hash TEXT NOT NULL,         -- sha256 or xxhash64 of content
  mtime TEXT NOT NULL,
  size INTEGER NOT NULL,
  last_indexed_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS codebase_chunks(
  id TEXT PRIMARY KEY,        -- ULID
  file_path TEXT NOT NULL,
  start_line INTEGER NOT NULL,
  end_line INTEGER NOT NULL,
  content TEXT NOT NULL,
  hash TEXT NOT NULL,
  FOREIGN KEY(file_path) REFERENCES codebase_files(path) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_chunks_path ON codebase_chunks(file_path);

CREATE VIRTUAL TABLE IF NOT EXISTS codebase_fts_porter USING fts5(
  id UNINDEXED, content, path, tokenize='porter unicode61 remove_diacritics 1'
);
CREATE VIRTUAL TABLE IF NOT EXISTS codebase_fts_trigram USING fts5(
  id UNINDEXED, content, path, tokenize='trigram'
);
```

`SqliteCodebaseIndex` owns creation, upsert, delete, and `DROP TABLE IF EXISTS` for any legacy leftovers. `CodebaseIndexService` sits on top and handles chunking, hashing, and FTS sync.

## 4. Chunking & FTS Pipeline

### 4.1 Ignore & filters (reuse `.gitignore`)

Default skip: `node_modules`, `.bin*`, `.next`, `obj`, `.git`, `.turbo`, `.eling`, `.artifacts`, `.vercel`, `out`, `build`, `dist`, plus respect the repo `.gitignore`, a 512KB per-file maxBytes cap, and binary detection (null bytes / non-UTF8). The Eling repo is small, but these rules keep the MVP cheap.

### 4.2 Chunking

Fixed window of 250–300 lines, 30-line overlap, split at line boundaries (not tokens). Store 1-based `start_line`/`end_line`. The overlap keeps context from being cut mid-function. A per-function alternative was considered and rejected for MVP: it needs a parser per language, is complex, and is not language-agnostic.

### 4.3 Indexing flow (incremental)

```
scan workspace → apply ignore rules → for each file:
  hash = sha256(content), mtime = File.GetLastWriteTimeUtc
  if codebase_files[path].hash == hash → skip
  else re-chunk → delete chunks where file_path=path
       → insert new chunks (ULID) → upsert FTS porter+trigram
       → upsert codebase_files
for paths in DB but not on disk → delete chunks + FTS + files row
```

`full:true` bypasses the hash check and rebuilds everything. Hashing keeps mixed mode cheap: a watcher event touching 3 files re-chunks only those 3 files.

### 4.4 Search scoring (reuse memory)

Identical to `MemoryRecallService`:
1. Porter `MATCH` with `AND` (precision)
2. If hits below threshold (e.g. <3), fall back to Porter `OR`
3. Still thin → fall back to trigram (typo tolerant)
4. Rank with BM25 (`rank` from FTS5), default limit 10, max 50.

No vector stage. Score and `fresh` are returned to the caller; `fresh` is derived from `max(last_indexed_at)` vs pending mtimes in `codebase_files`.

## 5. Watcher — Mixed Manual + Gentle Auto

### 5.1 Manual trigger

Tool `codebase_index` (see §6). Single-flight: only one indexing job runs; a second call while running → return `{ status: "already_indexing", hint: "check codebase_status" }` or enqueue. It never blocks `codebase_search` — search reads the latest snapshot.

### 5.2 Background watcher

`FileSystemWatcher` on the workspace root, `IncludeSubdirectories=true`, `NotifyFilter = LastWrite|FileName|DirectoryName`. Events are debounced 800ms–1s and batched (e.g. `ConcurrentQueue<string>` + `Timer`). A batch yields a `HashSet<path>` and runs the same incremental pass as manual, but only for paths in the batch. The watcher honors the same ignore rules and skips files above maxBytes or detected as binary.

Mode: on by default; there is no opt-out flag. The only control is the registration gate in `DashboardServices`, which starts the watcher solely on the dashboard owner of a real, non-excluded project session, so exactly one process indexes a given workspace in the background. User-home (global-only) and excluded (temporal) sessions never trail their working directory. Rationale: `eling-backend` is a single lightweight binary - one watcher is cheap, and an index that silently goes stale is the failure mode that actually costs an agent time. The cost of watching is bounded by the ignore rules, the 512KB cap, and binary detection already applied to every pass. With the watcher running, the search `fresh` flag is a backstop rather than the primary staleness signal. Watcher failure never affects search.

## 6. MCP Tools

One file per tool in `src/backend/Eling.Backend/Mcp/Tools/`, reusing the `MemoryRecallTool.cs` / `MemoryIndexTool.cs` pattern.

### 6.1 DI

```
src/backend/Eling.Core/Codebase/SqliteCodebaseIndex.cs    — DB layer
src/backend/Eling.Core/Codebase/CodebaseIndexService.cs   — index/search
src/backend/Eling.Core/Codebase/CodebaseWatcherService.cs — watcher (BackgroundService)
src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs     — register memory.db + codebase.db
```

Registration:
```csharp
services.AddSingleton<ICodebaseIndex>(new SqliteCodebaseIndex(Path.Combine(dataDir, "codebase.db")));
services.AddSingleton<CodebaseIndexService>();
services.AddHostedService<CodebaseWatcherService>(); // gated on the dashboard-owner session, no flag
```

### 6.2 `codebase_index`

```json
{
  "name": "codebase_index",
  "description": "Build or refresh the codebase FTS index (incremental by default, cache in .eling/codebase.db).",
  "inputSchema": {
    "type": "object",
    "properties": {
      "full": { "type": "boolean", "description": "true = rebuild all chunks, false = incremental by hash (default false)" },
      "paths": { "type": "array", "items": { "type": "string" }, "description": "optional scope, e.g. [\"src/backend\"]" }
    }
  }
}
```

Output:
```json
{ "indexedFiles": 142, "chunks": 380, "skipped": 120, "deleted": 2, "tookMs": 820, "fresh": true, "dbPath": ".eling/codebase.db" }
```

### 6.3 `codebase_search`

```json
{
  "name": "codebase_search",
  "description": "Search the codebase index (FTS Porter+trigram, BM25 ranking). Returns path + line range + snippet ready to inject.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "query": { "type": "string" },
      "limit": { "type": "integer", "minimum": 1, "maximum": 50, "default": 10 },
      "pathPrefix": { "type": "string", "description": "filter by path prefix, e.g. src/backend" },
      "filePattern": { "type": "string", "description": "glob-ish, e.g. *.cs or *.md" }
    },
    "required": ["query"]
  }
}
```

Internal flow: `CodebaseIndexService.SearchAsync(query, limit, pathPrefix, filePattern)` → Porter AND → OR → trigram → BM25 → project into `hits`.

Output:
```json
{
  "hits": [
    { "path": "src/backend/Eling.Core/Scope/ProjectScope.cs", "startLine": 42, "endLine": 88, "content": "class ProjectScope ...", "score": 12.34 }
  ],
  "stats": { "totalHits": 12, "tookMs": 18, "fresh": true }
}
```

`content` is the chunk snippet, directly injectable without an extra `file_read`. `fresh:false` means modified files are not yet re-indexed.

### 6.4 `codebase_status`

```json
{ "name": "codebase_status", "inputSchema": { "type": "object", "properties": {} } }
```

Output:
```json
{ "lastIndexedAt": "2026-09-22T08:00:00Z", "fileCount": 142, "chunkCount": 380, "watcherActive": true, "pendingFiles": 0, "dbPath": ".eling/codebase.db", "memoryDbPath": ".eling/memory.db" }
```

### 6.5 Errors & telemetry

Use the same error shape as `MemoryWriteTool` (typed errors, audit trail where applicable). Telemetry via the existing MCP telemetry used by `FileSystemTools`. `codebase_search` must never throw when the DB is missing — if `codebase.db` does not exist, return `hits: []` + `stats.fresh=false` + hint "run codebase_index".

## 7. Gitignore & Compatibility

`.gitignore`:
```
# Databases / Caches — covers every .eling/ database too, at any depth
*.db
*.db-*
# Eling runtime
.eling/index.db*   # legacy transition — keep until next release
.eling/runtime/
```

A general pattern rather than per-database lines. Every SQLite sidecar is
`<name>.db-<suffix>` — `-journal`, `-wal`, `-shm`, `-wal2` — so `*.db-*` stays
complete as SQLite adds more, and it does not over-match unrelated names such as
`*.dbf` or `*.dbg`. Naming each database explicitly was rejected as the fastest
way to go stale: a new `codebase2.db` would silently be untracked, and nothing
would prompt anyone to add a line for it. The concrete names already live in
`ElingPaths.MemoryDbFileName` / `CodebaseDbFileName` as constants.

Note this corrects an omission: the pre-existing `.gitignore` had no rule for
`*.db-shm` at all, even though the index runs in WAL mode
(`PRAGMA journal_mode=WAL`) and WAL always creates a shared-memory sidecar. So
`memory.db-shm` and `codebase.db-shm` would have surfaced as untracked. The
advertised standard in `ServerInstructions.cs` carried the same gap and was fixed
alongside it.

No new files become tracked. `.eling/memories/` remains the only tracked payload. Documentation and `recall-architecture.md` are updated to mention `memory.db` + `codebase.db`. The installer needs no changes — both databases are created on demand.

## 8. Risks & Mitigations

- **DB bloat on large repos** — mitigations: 512KB maxBytes, binary detection, aggressive ignore, chunk cap; a separate `codebase.db` cannot bloat `memory.db`.
- **Watcher misses / buffer overflow** — `FileSystemWatcher` can overflow on Windows bursts — mitigations: debounce+batch, full incremental rescan on the `Error` event (overflow), and an explicit `fresh` flag.
- **Encoding / CRLF** — read as tolerant UTF-8, compute `start_line` on `\n`, store snippet content verbatim.
- **Relevance vs precision** — FTS is less precise than LSP for `goto_definition` — mitigation: document the limitation, schedule LSP as phase 2, not MVP.
- **Concurrency** — `SemaphoreSlim` single-flight for the index job; search is always read-only and non-blocking.
- **Migration failure** — if `index.db` is locked during move — mitigations: `File.Copy`+delete fallback, or allow both files to live and use a fresh `memory.db` (rebuild) while logging a warning; no truth is lost because markdown remains on disk.

## 9. Implementation Plan

Ordered implementation (estimated 1.5–2 days for MVP):

1. **Rename** — `McpServiceExtensions.cs` switch to `memory.db`, add the `File.Move` migration, update `.gitignore`, update tests asserting paths.
2. **Storage** — `SqliteCodebaseIndex.cs` + tables/FTS, `ICodebaseIndex` interface.
3. **Service** — `CodebaseIndexService.cs` (scan, hash, chunk, FTS upsert, search with Porter→trigram fallback).
4. **Tools** — `CodebaseIndexTool.cs`, `CodebaseSearchTool.cs`, `CodebaseStatusTool.cs` (one file per tool).
5. **Watcher** — `CodebaseWatcherService.cs` debounced, conditional registration, `codebase_status.watcherActive`.
6. **Docs & tests** — update `recall-architecture.md`, add unit tests for chunk overlap, hash incrementality, and search fallback.

Files touched/added:
- Edit: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`, `.gitignore`, `docs/recall-architecture.md`
- New: `src/backend/Eling.Core/Codebase/ICodebaseIndex.cs`, `SqliteCodebaseIndex.cs`, `CodebaseIndexService.cs`, `CodebaseWatcherService.cs`, `src/backend/Eling.Backend/Mcp/Tools/CodebaseIndexTool.cs`, `CodebaseSearchTool.cs`, `CodebaseStatusTool.cs`
- Tests: `tests/Eling.Core.Tests/Codebase/*`

## 10. Success Criteria

- Incremental `codebase_index` on the Eling repo (~150 files) finishes in <2s, full rebuild in <5s; `codebase_search` p95 <50ms.
- `codebase_search("project scope policy")` finds `Scope/ProjectScope.cs` in the top 3 while `memory_recall` still behaves as before.
- `.eling/memory.db` and `.eling/codebase.db` are git-ignored; deleting both and running `codebase_index` again reproduces identical hits.
- An existing install with `index.db` migrates to `memory.db` automatically with no manual step.
- Watcher on by default does not interfere; three consecutive file saves trigger a single batched re-index.

## 11. Future — LSP Phase 2

If broad FTS proves too imprecise for navigation, add a per-language sidecar LSP (Roslyn for `src/backend`, `typescript-language-server` for `src/frontend`) behind new tools `codebase_goto_definition` / `codebase_find_references`. Not mixed into MVP — this design deliberately leaves the door open: `codebase.db` stays for broad search, LSP for symbol precision.

## 12. Cross-Project Search (Sibling Scope)

`codebase_search` can read **sibling** indexes — other projects that are neither ancestor nor descendant — but only for user-approved siblings. Default stays isolated per project, mirroring the memory-scope tenet: ownership boundary, not a lens.

### 12.1 Registry

- Sibling grants live in **global config** (`~/.config/eling/config/siblings.json`), never inside a repo:
  ```json
  [ { "root": "/abs/path/to/project-b", "enabled": true } ]
  ```
- Grants change only via MCP tools `codebase_sibling_add` / `codebase_sibling_remove` / `codebase_sibling_list`, approval-gated like `memory_copy_to_project` (the MCP sandbox `FileSystemService` has no `folder_select`, so "grant" == registry entry written through the tool, not a browser path).
- No registry entry → sibling DB is invisible; no path parameter on `codebase_search` can bypass this.

### 12.2 Federated search

- `codebase_search({ query, limit, siblings = false })` — `siblings` defaults `false`.
- When `siblings = true`: for each enabled registry root, open `root/.eling/codebase.db` **read-only** (same `SqliteCodebaseIndex` connection style; no writes, no watcher), run Porter AND → OR → trigram per project, merge hits, attach provenance `{ projectRoot, path, startLine, endLine, content, score }`.
- Ranking: global BM25 + locality bias ×1.1 to the current project (local wins ties).
- Sibling DB missing → skipped; stale root (moved/deleted) → `stats.skipped: [roots]`, never throws.
- Current project always searched even when `siblings: true`.

### 12.3 Phasing & safety

- **MVP ships only the design-compatible seams**: `SqliteCodebaseIndex` openable read-only on an arbitrary path + provenance `projectRoot` attached at merge time. No registry, no new tools, no cross-DB loop.
- **Phase 2 (alongside LSP)**: registry file + `codebase_sibling_*` tools + federation loop.
- Sibling connections are read-only and sandboxed to registry roots; watcher/indexer never touch siblings.

## 13. Decision Log

- 2026-09-27 — **Watcher default reversed; opt-out flag removed** — the opt-in decision below is superseded. `ELING_CODEBASE_WATCHER` is gone from the code, so there is no way to disable the watcher at all; the `isOwnerMode && !IsUserHome && !IsCodebaseExcluded` registration gate in `DashboardServices` is the only control. Reason: the original rationale ("it should not run before it is stable") no longer holds now that the watcher is on the critical path of every session, and a stale index is the failure mode that actually costs an agent time — an agent that searches, gets stale hits, and reasons from them. Accepting the cost: `CodebaseWatcherService` has no automated test for the debounce/drain path beyond `CodebaseWatcherServiceTests`, and `fresh`/`pendingFiles` are still reported as constants rather than computed (§4.4), so the `fresh` flag is not yet a trustworthy backstop. Both are tracked as follow-ups rather than blockers for the flag removal itself (user decision).

- 2026-09-22 — Cross-project (sibling) search accepted into spec as §12: approval-gated global registry, read-only federation, isolated by default; MVP ships seams only, registry + tools deferred to phase 2 (user decision).

- 2026-09-22 — Cross-project (sibling) search accepted into spec as §12: approval-gated global registry, read-only federation, isolated-by-default; MVP ships seams only, registry + `codebase_sibling_*` tools deferred to phase 2 with LSP (user decision).

- **Separate `memory.db` + `codebase.db` vs single DB** — chose separation. Isolation, explicit naming, and the existing `.eling/*.db` wildcard.
- **FTS reuse vs vector embedding** — chose FTS reuse. Cheapest, language-agnostic, reuses the battle-tested BM25 AND→OR fallback in `SqliteMemoryIndex`.
- **Fixed-window chunking vs per-function** — chose fixed-window 250–300 with 30-line overlap. Simple, reusable, no per-language parser.
- **Mixed watcher vs manual only** — chose mixed with a debounced watcher, originally opt-in. **Superseded 2026-09-27**: the watcher is now on by default and the flag is removed. Manual remains primary; the watcher syncs gently without forcing real-time behavior.
