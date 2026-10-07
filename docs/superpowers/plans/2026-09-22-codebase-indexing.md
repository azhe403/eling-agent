# Codebase Indexing via MCP + index.db → memory.db Rename

> **Status**: IMPLEMENTED — shipped 2026-09-29. The unchecked boxes below are stale:
> this plan was executed, but its steps were never ticked. See the
> "Implementation note" under the spec's status header for the one design
> divergence (codebase DB moved to a global per-workspace store).
> **Spec**: `docs/superpowers/specs/2026-09-22-codebase-indexing-design.md`
> **Context**: Eling memory FTS (Porter+trigram+BM25) exists for memories; codebase files have no index. This plan adds codebase indexing as MCP tools (mixed manual + debounced watcher) and renames `index.db` → `memory.db` while introducing a separate `codebase.db`.

## Goal

Give agents two new MCP tools — `codebase_index` and `codebase_search` (plus `codebase_status`) — that index workspace files into `.eling/codebase.db` (FTS cache) and return BM25-ranked snippets ready to inject, while renaming the memory cache `index.db` → `memory.db` transparently for existing installs.

## Non-Goals

- LSP / symbol precision, vector embeddings, CLI command, indexing outside workspace root.

## File Structure

| File | Responsibility |
|------|----------------|
| `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs` | *(edit)* swap `index.db` → `memory.db` at 5 sites + one-time `File.Move` migration; register `ICodebaseIndex` + `CodebaseIndexService` + conditional watcher |
| `src/backend/Eling.Core/Codebase/ICodebaseIndex.cs` | *(new)* DB contract: `EnsureCreatedAsync`, `UpsertFileAsync`, `DeleteFileAsync`, `ReplaceChunksAsync`, `GetFileAsync`, `ListFilesAsync`, `SearchPorterAsync`, `SearchTrigramAsync`, `ClearAsync` |
| `src/backend/Eling.Core/Codebase/SqliteCodebaseIndex.cs` | *(new)* `codebase_files` + `codebase_chunks` + two FTS5 tables; mirrors `SqliteMemoryIndex.cs` connection style (WAL, single connection pattern) |
| `src/backend/Eling.Core/Codebase/CodebaseIndexService.cs` | *(new)* scan + ignore rules + hash + chunking + FTS sync; search with Porter AND → OR → trigram fallback; single-flight via `SemaphoreSlim` |
| `src/backend/Eling.Core/Codebase/CodebaseChunker.cs` | *(new, static)* pure function `Chunk(string content) → IEnumerable<(startLine, endLine, text)>` — 250-line windows, 30 overlap; isolated for unit tests |
| `src/backend/Eling.Core/Codebase/CodebaseWatcherService.cs` | *(new)* `BackgroundService`: `FileSystemWatcher` + 900ms debounce batch → calls incremental index; gated on the dashboard-owner session; no opt-out flag |
| `src/backend/Eling.Backend/Mcp/Tools/CodebaseIndexTool.cs` | *(new)* MCP tool `codebase_index` (`full?`, `paths?`) |
| `src/backend/Eling.Backend/Mcp/Tools/CodebaseSearchTool.cs` | *(new)* MCP tool `codebase_search` (`query`, `limit?`, `pathPrefix?`, `filePattern?`) |
| `src/backend/Eling.Backend/Mcp/Tools/CodebaseStatusTool.cs` | *(new)* MCP tool `codebase_status` — `lastIndexedAt`, counts, `watcherActive`, `pendingFiles` |
| `docs/recall-architecture.md` | *(edit)* mention `memory.db` + `codebase.db` + two new tools |
| `.gitignore` | *(edit)* explicit `.eling/memory.db*`, `.eling/codebase.db*`; keep legacy `.eling/index.db*` |
| `tests/Eling.Core.Tests/Codebase/CodebaseChunkerTests.cs` | *(new)* window/overlap/line-number tests |
| `tests/Eling.Core.Tests/Codebase/SqliteCodebaseIndexTests.cs` | *(new)* table create, upsert, delete, FTS round-trip |
| `tests/Eling.Core.Tests/Codebase/CodebaseIndexServiceTests.cs` | *(new)* incremental hash skip, ignore rules, search fallback |
| `tests/Eling.Backend.Tests/Mcp/MemoryDbRenameTests.cs` | *(new)* migration `index.db` → `memory.db` |
| `tests/Eling.Backend.Tests/CodebaseWatcherServiceTests.cs` | *(new)* watcher activation with no flag, drained batch reaching the index, enqueue filter dropping excluded dirs |

**Tool wiring note:** every existing tool is one file under `Mcp/Tools/` registered in the MCP tool collection — follow `MemoryRecallTool.cs` / `MemoryIndexTool.cs` for `[McpServerTool]` attribute style, input schema JSON, and error shape.

**Test project note:** confirm actual test project paths under `tests/` before Task 3 (adjust if the repo uses `Eling.Core.Tests` elsewhere or a different naming).

## Tasks

### Task 1 — Rename `index.db` → `memory.db` + migration

1. Edit `McpServiceExtensions.cs`: replace all 5 `Path.Combine(..., "index.db")` with `"memory.db"` (lines ~35, 41, 90, 102, 119).
2. Add private static helper `MigrateLegacyIndexDb(string dataDir)`:
   ```csharp
   static void MigrateLegacyIndexDb(string dataDir)
   {
       var memory = Path.Combine(dataDir, "memory.db");
       var legacy = Path.Combine(dataDir, "index.db");
       if (File.Exists(memory) || !File.Exists(legacy)) return;
       foreach (var suffix in new[] { "", "-wal", "-shm" })
       {
           var src = legacy + suffix;
           if (!File.Exists(src)) continue;
           var dst = memory + suffix;
           try { File.Move(src, dst, overwrite: true); }
           catch (IOException) { /* locked: fall back to fresh memory.db, log warning */ }
       }
   }
   ```
   Call it once before constructing each `SqliteMemoryIndex`.
3. `.gitignore`: add `.eling/memory.db*` (keep `.eling/index.db*` for transition; wildcard `.eling/*.db` already covers all).
4. Test (`MemoryDbRenameTests.cs`): arrange temp dir with dummy `index.db` bytes → call migration → assert `memory.db` exists and `index.db` gone; idempotent second call; missing-legacy no-op.
5. Run: `dotnet test --filter MemoryDbRenameTests`. Commit: `refactor(storage): rename index.db to memory.db with transparent migration`.

### Task 2 — SqliteCodebaseIndex (DB layer)

1. Create `ICodebaseIndex.cs` + `SqliteCodebaseIndex.cs` in `src/backend/Eling.Core/Codebase/`.
2. DDL exactly as spec §3.3 (`codebase_files`, `codebase_chunks` + FK cascade + `idx_chunks_path`, `codebase_fts_porter`, `codebase_fts_trigram`). Mirror `SqliteMemoryIndex.cs`: WAL pragma, `busy_timeout`, parameterized SQL, dispose pattern.
3. Methods:
   - `EnsureCreatedAsync()` — run DDL idempotently.
   - `GetFileAsync(path)` → `CodebaseFileRecord?` (hash/mtime/size/lastIndexedAt).
   - `ReplaceChunksAsync(path, chunks)` — transaction: delete old chunk rows + FTS rows for `file_path`, insert new, upsert `codebase_files`.
   - `DeleteFileAsync(path)` — remove file row + chunks + FTS (both tables).
   - `SearchPorterAsync(query, limit, pathFilter?, patternFilter?)` / `SearchTrigramAsync(...)` → `IReadOnlyList<CodebaseHit>` with `bm25` score.
   - `ClearAsync()` for rebuild.
4. Test (`SqliteCodebaseIndexTests.cs`, temp-dir DB): create → upsert file+chunks → search hit by content → mutate → old hit gone new hit present → delete file → zero hits.
5. Run: `dotnet test --filter SqliteCodebaseIndexTests`. Commit: `feat(codebase): SqliteCodebaseIndex storage + FTS tables`.

### Task 3 — Chunker (pure, TDD)

1. Write failing tests first (`CodebaseChunkerTests.cs`):
   - 600-line input → chunks cover lines 1..600 exactly, no gaps, no overlap in *span* (windows overlap in content but line coverage is contiguous).
   - Every chunk ≤ 250 lines; consecutive `startLine` diff ≤ 220 (30-line overlap).
   - Single-line and empty input → one chunk / zero chunks.
   - `endLine` ≤ total line count.
2. Implement `CodebaseChunker.Chunk` (split on `\n`, keep `\r` tolerance, 1-based lines).
3. Run: `dotnet test --filter CodebaseChunkerTests`. Commit: `feat(codebase): fixed-window chunker with overlap`.

### Task 4 — CodebaseIndexService (scan + hash + fallback search)

1. Constructor takes `ICodebaseIndex` + workspace root.
2. `IndexAsync(full, paths?, IProgress<string>? = null, CancellationToken)`:
   - Enumerate files (respect ignore list from spec §4.1: `node_modules`, `.bin*`, `.next`, `obj`, `.git`, `.turbo`, `.eling`, `.artifacts`, `.vercel`, `out`, `build`, `dist` + repo `.gitignore` if parseable cheaply, else skip parser and rely on list; 512KB cap; UTF-8 binary sniff).
   - Hash = SHA256 hex of bytes; compare to `codebase_files.hash`; skip if equal unless `full`.
   - Re-chunk → `ReplaceChunksAsync`; sweep DB paths missing on disk → `DeleteFileAsync`.
   - Single-flight: if `SemaphoreSlim.Wait(0)` fails → return `IndexResult.AlreadyRunning`.
3. `SearchAsync(query, limit, pathPrefix?, filePattern?)`:
   - Porter AND → if hits < 3 → Porter OR (append `*` OR-join) → if still < 3 → trigram.
   - Return hits + `fresh` (pending = files whose DB mtime < disk mtime) + `tookMs`.
4. `GetStatusAsync()` → counts, `lastIndexedAt`, `pendingFiles`, `watcherActive`.
5. Tests (`CodebaseIndexServiceTests.cs`, temp workspace with fake `.cs`/`.md` files):
   - First index → fileCount > 0; second index → all `skipped`, `took` ≪ first.
   - Edit one file → only that file re-indexed (`indexedFiles == 1`).
   - Delete file → row gone.
   - Ignore: create `node_modules/x.txt` → never indexed.
   - Search fallback: rare-token query returns hits via OR/trigram path; nonexistent token → empty + `fresh` flag present.
6. Run: `dotnet test --filter CodebaseIndexServiceTests`. Commit: `feat(codebase): incremental index service with FTS fallback search`.

### Task 5 — MCP tools (index / search / status)

1. `CodebaseIndexTool.cs` — schema `{ full?: bool, paths?: string[] }` per spec §6.2; output `{ indexedFiles, chunks, skipped, deleted, tookMs, fresh, dbPath }` (+ `status:"already_indexing"` branch).
2. `CodebaseSearchTool.cs` — schema per §6.3 (`query` required, limit 1..50 default 10); output `{ hits[], stats{ totalHits, tookMs, fresh } }`; **never throws on missing DB** → `hits:[]` + hint.
3. `CodebaseStatusTool.cs` — §6.4 output.
4. Register all three where existing tools are registered (same collection in `McpServiceExtensions` / tool assembly scan — match how `MemoryRecallTool` is discovered).
5. Smoke (integration-style test or manual against dev server 4417): `codebase_index` full → `codebase_search("project scope policy", limit 5)` → top hit under `src/backend/Eling.Core/Scope/` or `docs/`. Run `dotnet test`. Commit: `feat(mcp): codebase_index/codebase_search/codebase_status tools`.

### Task 6 - Watcher (mixed mode, on by default)

1. `CodebaseWatcherService : BackgroundService`:
   - No gate inside the service: once registered, the watcher always runs. Registration itself is the only control (see step 2).
   - `FileSystemWatcher` on workspace root, `IncludeSubdirectories = true`, `NotifyFilter = LastWrite | FileName | DirectoryName`.
   - Events enqueue paths into `ConcurrentQueue<string>`; 900ms debounce `Timer` drains queue into `HashSet` → `IndexAsync(full:false, paths)`.
   - On watcher `Error` (buffer overflow) → trigger full incremental rescan.
   - Watcher failure logged, never crashes host or blocks search.
2. Register: `if (isOwnerMode && !context.IsUserHome && !ElingPaths.IsCodebaseExcluded(context.Chain.Cwd)) services.AddHostedService<CodebaseWatcherService>();` — also expose `watcherActive` into `GetStatusAsync`.
3. Test: `tests/Eling.Backend.Tests/CodebaseWatcherServiceTests.cs` covers activation with no env flag, the removed opt-out flag having no effect, a drained batch reaching the index, and the enqueue filter dropping an excluded dir. The debounce drain is asserted by polling to a deadline, not by sleeping a fixed span, so a slow machine widens the window instead of failing. Not covered: the `Error`-event overflow rescan, which cannot be triggered deterministically. Run `dotnet test`. Commit: `feat(codebase): debounced file watcher`.

### Task 7 — Docs, gitignore final, full verification

1. `docs/recall-architecture.md`: add paragraph — `memory.db` renamed from `index.db`; `codebase.db` separate cache; tools `codebase_index` / `codebase_search` / `codebase_status`; watcher flag.
2. `.gitignore` final pass per spec §7 (explicit lines).
3. Grep repo for leftover `index.db` references in **code** (docs/plans historical stay): `rg '"index\.db"' src tests`.
4. Full gate:
   ```
   dotnet test
   git status --short   # only intended files staged
   ```
5. Commit: `docs: codebase indexing + memory.db rename notes`.

## Definition of Done

- All tests green (`dotnet test`), no leftover `"index.db"` string in `src/` or `tests/`.
- Fresh `.eling/` (empty) → `codebase_index` → `codebase_search("project scope policy")` returns ≥3 hits with line ranges; deleting `codebase.db` and re-indexing reproduces hits.
- Existing install with `index.db` auto-migrates on boot (Task 1 test).
- Watcher on by default; rapid saves → one batched re-index.
- `.eling/memory.db*` and `.eling/codebase.db*` ignored by git; `.eling/memories/` still tracked.
- Spec and plan remain English; commit bundled only after user approves plan (spec + plan together).

## Execution Notes (for zero-context implementer)

- Reuse patterns from: `SqliteMemoryIndex.cs` (FTS dual-table, BM25, fallback scoring), `MemoryRecallTool.cs` (tool shape), `McpServiceExtensions.cs` (DI + data-dir resolution incl. global/chain scopes).
- Dev servers: backend `eling_dev` → 4417 for manual MCP smoke; frontend not involved.
- TDD order inside each task: tests first where marked (Tasks 1–4), tools/watcher (5–6) integration-style after service exists.
- Stop on any test failure — report, do not auto-fix beyond the task's own code.
