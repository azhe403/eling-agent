# LSP-Based Indexing (Chunking + File Relations) — Design

**Date:** 2026-10-18
**Status:** Proposed (awaiting user review)
**Scope:** `Eling.Core` (new LSP client layer, symbol extractor, relation store), `Eling.Backend` (MCP tools, watcher integration), tests
**Related:** `docs/superpowers/specs/2026-09-22-codebase-indexing-design.md`, `docs/superpowers/specs/2026-10-18-per-method-chunking-design.md`

## 1. Goal

Replace the brace-counting chunker and add file-relation extraction using **Language Server Protocol (LSP)** so that:
- Chunk boundaries are semantic (real method/class/property spans), not brace heuristics.
- A file→file dependency graph (imports, type references, calls) is captured and queryable.
- Multi-language support is uniform (one protocol, many servers).

## 2. Non-Goals (this iteration)

- No visual graph UI in dashboard (relations exposed via MCP tools + existing codebase page).
- No cross-project symbol resolution (per-workspace only).
- No refactoring/rename features (read-only indexing only).
- No custom LSP server authoring.

## 3. Architecture

### 3.1 Layers

```
┌─────────────────────────────────────────────┐
│  MCP Tools (codebase_index, codebase_search,  │
│  codebase_dependencies, codebase_status)      │
├─────────────────────────────────────────────┤
│  CodebaseIndexService (orchestrator)          │
│   - scan, hash, chunk, extract relations     │
├──────────────┬──────────────────────────────┤
│  Chunker     │  Relation Extractor          │
│  (LSP-first, │  (LSP-first,                 │
│   fallback   │   fallback regex)            │
│   brace)     │                              │
├──────────────┴──────────────────────────────┤
│  LSP Client Layer (JSON-RPC over stdio)      │
│   - server registry, lifecycle, document sync│
├─────────────────────────────────────────────┤
│  Storage (SQLite): codebase_chunks,           │
│  codebase_files, codebase_file_deps           │
└─────────────────────────────────────────────┘
```

### 3.2 LSP Client Layer

- `ILspClient` interface: `InitializeAsync`, `DidOpenAsync`, `DidChangeAsync`, `DocumentSymbolsAsync`, `ReferencesAsync`, `ShutdownAsync`.
- `LspClient` (JSON-RPC over stdio): spawns server process, manages lifecycle, handles crashes with restart (max 3 retries).
- `LspServerRegistry`: maps file extension → server command + args (configurable via `~/.eling/lsp-servers.json`, eling JSON-store style).
- Default servers:
  - `.cs` → **Roslyn Language Server** (`roslyn-language-server`) or `csharp-ls` (dotnet tool). External process keeps the backend lightweight; no in-process Roslyn.
  - `.ts`/`.tsx`/`.js`/`.jsx` → `typescript-language-server`
  - `.go` → `gopls`
  - `.rs` → `rust-analyzer`
  - `.java` → `jdtls` (or skip, heavy)
  - `.py` → `pyright` / `pylsp` (optional)
- Graceful degradation: if server binary not found or fails to start, fall back to brace-scanner chunking + regex relations. Never block indexing.

### 3.3 C# via External Roslyn LSP (no in-process Roslyn)

User decision: heavy parsing runs in a **separate process**, not in the backend. So even C# uses an external LSP server (`roslyn-language-server` or `csharp-ls`), not in-process `Microsoft.CodeAnalysis`.

- Spawn server on-demand per workspace (see §3.4), communicate via LSP (`textDocument/documentSymbol`, `textDocument/references`).
- No Roslyn NuGet dependency in `Eling.Core` → backend stays light.
- Available on this machine: `roslyn-language-server` (already installed). Fallback: `csharp-ls` (installable via `dotnet tool install -g csharp-ls`).
- If no C# server available → fall back to brace-scanner chunking + regex relations (graceful).

### 3.4 Per-Workspace Lifecycle & Auto-Install

- **One server per workspace**: LSP server needs project context (csproj/solution); cannot serve multiple workspaces. Each workspace gets its own process.
- **Spawn on-demand**: server started only when indexing that workspace; shut down after the pass (or after idle timeout, e.g. 5 min).
- **Idle shutdown**: background watcher holds the server only while a batch is being processed; otherwise the process exits.
- **Graceful concurrency**: if multiple workspaces index simultaneously, each spawns its own process; watcher serialization (owner-only) keeps this rare in practice.
- **Auto-install (best-effort)**: if server binary missing, try package-manager install (`dotnet tool install -g csharp-ls`, `npm install -g typescript-language-server`, `go install golang.org/x/tools/gopls@latest`, etc.). On failure, log and fall back to brace-scanner/regex; show manual install hint. Never block indexing.

### 3.5 Chunking (LSP-first)

1. Try LSP `textDocument/documentSymbol` → hierarchical symbols → flatten to methods/properties/classes → each becomes a chunk (span = symbol range).
2. If LSP unavailable or returns empty → fall back to current `DeclarationScanner` (brace) → `MemberChunks`.
3. If no symbols → `PackedChunks` (fixed windows).

Oversized symbol (> `MaxChunkLines`) still split with overlap.

### 3.6 Relation Extraction

New table:
```sql
CREATE TABLE IF NOT EXISTS codebase_file_deps (
    source_path TEXT NOT NULL,
    target_path TEXT NOT NULL,
    kind TEXT NOT NULL,       -- 'import', 'type-ref', 'call', 'inheritance'
    symbol TEXT,              -- optional symbol name
    PRIMARY KEY (source_path, target_path, kind, symbol)
);
CREATE INDEX idx_deps_source ON codebase_file_deps(source_path);
CREATE INDEX idx_deps_target ON codebase_file_deps(target_path);
```

Extraction per file:
- **Import/using**: `using X;` / `import X` / `#include` / `require` → resolve to file path (heuristic: namespace → folder, or relative path).
- **Type references**: LSP `textDocument/references` or Roslyn symbol resolution → declaring file.
- **Inheritance**: `class A : B` → B's declaring file.

Stored as edges. Queried via new MCP tool `codebase_dependencies(path, direction, depth)`.

### 3.7 MCP Tools

| Tool | Change |
|---|---|
| `codebase_index` | Add `lsp?: bool` (default true, opt-out). Server startup errors logged, never fail index. |
| `codebase_search` | Add `expand?: bool` (default false) → include 1-hop related files in results. |
| `codebase_dependencies` | **New**: `{ path, direction: "out"\|"in"\|"both", depth: 1..3 }` → returns file dependency graph. |
| `codebase_status` | Add `lspServers: { ext: "running"\|"missing"\|"error" }`. |

### 3.8 Storage (ICodebaseIndex additions)

- `ReplaceDepsAsync(sourcePath, edges)` — transaction: delete old edges for source, insert new.
- `GetDepsAsync(path, direction, depth)` — recursive CTE or iterative.
- `DeleteDepsAsync(path)` — on file delete.

### 3.9 Watcher Integration

Watcher unchanged (file events → incremental index). LSP document sync (`didOpen`/`didChange`/`didClose`) happens inside `CodebaseIndexService.IndexFileAsync` when LSP is enabled. On watcher batch, sync changed files.

### 3.10 Configuration

LSP server config uses eling's JSON file store pattern: a file at `{eling-user-scope}/lsp-servers.json` (e.g. `~/.eling/lsp-servers.json`), managed like `ProviderStore`/`JsonDatabaseConnectionStore`.

```json
{
  "enabled": true,
  "serverStartupTimeoutMs": 10000,
  "fallbackToBraceScanner": true,
  "servers": {
    ".cs": { "command": "roslyn-language-server", "args": ["--stdio"] },
    ".ts": { "command": "typescript-language-server", "args": ["--stdio"] },
    ".go": { "command": "gopls", "args": ["serve"] },
    ".rs": { "command": "rust-analyzer", "args": [] }
  }
}
```

Defaults are auto-populated by detecting binaries on `PATH` (roslyn-language-server, gopls, rust-analyzer are already present on this machine). Missing servers trigger best-effort auto-install (`dotnet tool install`, `npm install -g`, `go install`, etc.); on failure, fall back to brace-scanner and show a manual install hint.

C# uses external `roslyn-language-server` (or `csharp-ls`) via `servers[".cs"]`; no in-process Roslyn. If the server is missing, falls back to brace-scanner.

## 4. Implementation Phases

### Phase 1 — LSP Client + Roslyn Server (C#) + Relation Extraction
- `LspClient` (JSON-RPC over stdio): spawn `roslyn-language-server --stdio`, initialize, didOpen/didChange/didClose, documentSymbol, references, shutdown. Crash recovery (restart max 3×).
- `LspChunker`: documentSymbol → flatten method/property/class → chunk spans. Fallback to brace-scanner if server unavailable.
- `LspRelationExtractor`: references + using → file→file edges.
- `CodebaseIndexService`: for `.cs` use LSP client; others use brace-scanner/regex. Relations extracted during index.
- Storage: add `codebase_file_deps` table + `ICodebaseIndex` methods (ReplaceDepsAsync, GetDepsAsync, DeleteDepsAsync).
- Tests: LspClientTests (mock process), LspChunkerTests, LspRelationExtractorTests, integration test with real server.

No Roslyn NuGet dependency (external process). Backend stays light.

### Phase 2 — LSP Client Layer + Multi-Language
- `LspClient` (JSON-RPC stdio), `LspServerRegistry`, lifecycle management.
- `LspChunker` + `LspRelationExtractor` using `documentSymbol` + `references`.
- Config-driven server registration.
- Graceful fallback when server missing.

### Phase 3 — MCP Tools + Search Expansion
- `codebase_dependencies` tool.
- `codebase_search` `expand` parameter.
- `codebase_status` LSP server status.

### Phase 4 — Dashboard (optional, later)
- Dependency list in existing codebase page (no new page).

## 5. Files (Phase 1 only)

| File | Change |
|---|---|
| `src/backend/Eling.Core/Codebase/LspClient.cs` | **new** — JSON-RPC over stdio, lifecycle, crash recovery |
| `src/backend/Eling.Core/Codebase/ILspClient.cs` | **new** — interface |
| `src/backend/Eling.Core/Codebase/LspServerRegistry.cs` | **new** — ext→command mapping + auto-install |
| `src/backend/Eling.Core/Codebase/LspChunker.cs` | **new** |
| `src/backend/Eling.Core/Codebase/LspRelationExtractor.cs` | **new** |
| `src/backend/Eling.Core/Codebase/IFileChunker.cs` | **new** — chunker interface |
| `src/backend/Eling.Core/Codebase/ICodebaseIndex.cs` | add dep methods |
| `src/backend/Eling.Core/Codebase/SqliteCodebaseIndex.cs` | add `codebase_file_deps` DDL + methods |
| `src/backend/Eling.Core/Codebase/CodebaseIndexService.cs` | chunker selection + relation extraction |
| `src/backend/Eling.Backend/Mcp/Tools/CodebaseDependenciesTool.cs` | **new** (Phase 3) |
| tests | new test classes |

## 6. Risks

- **Roslyn solution context**: full symbol resolution needs a `Solution`/`Project` from MSBuild. Without it, type→file resolution is heuristic (namespace→folder). Accept heuristic for MVP.
- **LSP server availability**: not all users have servers installed. Graceful fallback mandatory.
- **Performance**: Roslyn parsing per file is slower than brace-scanning. Benchmark before committing. If too slow, cache syntax trees.
- **Storage growth**: deps table adds rows but content is small (path pairs). Modest.

## 7. Decision

Awaiting user approval. If approved, start with **Phase 1 (LSP client + Roslyn server for C#)**, then evaluate whether to add more languages (Phase 2).