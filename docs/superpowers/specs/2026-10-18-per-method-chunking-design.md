# Per-Method Codebase Chunking — Design

**Date:** 2026-10-18
**Status:** Implemented
**Scope:** `Eling.Core` (`CodebaseChunker`, `DeclarationScanner`), tests in `Eling.Core.Tests`
**Related:** `docs/superpowers/specs/2026-09-22-codebase-indexing-design.md` §4.2 (original rejection of per-function chunking)

## 1. Goal

Make each method/member of a brace-delimited source file its own retrieval chunk, so a
query for a specific method returns that method directly instead of a 280-line window
that happens to contain it. This realizes the "chunk codebase per method" idea that the
original spec considered and deferred for MVP.

## 2. Non-Goals

- No AST/parser dependency. Reuse the existing brace scanner (`DeclarationScanner`).
- No change to storage schema, FTS tables, or MCP tool contracts.
- No change to prose/markdown or non-brace-language chunking.

## 3. Background

The original spec (§4.2) chose fixed 250–300 line windows with 30-line overlap and
explicitly rejected per-function chunking: "it needs a parser per language, is complex,
and is not language-agnostic." A later change (`7943489`, 2026-10-07) introduced
`DeclarationScanner` and made chunks *member-aware* — cuts land on member boundaries —
but still *packed* multiple members up to `TargetLines` (280). This change removes the
packing for brace languages: one member = one chunk.

## 4. Design

### 4.1 Strategy by file type

| File type | Strategy |
|---|---|
| Brace languages (`.cs`, `.ts`, `.tsx`, `.js`, `.jsx`, `.java`, `.go`, `.rs`) with balanced braces | One chunk per member boundary from `DeclarationScanner`. Preamble (usings, namespace, class header) attaches to the first member. |
| Brace languages with unbalanced braces | `PackedChunks` with no boundaries → fixed 280-line windows (unchanged fallback). |
| Prose / markdown | `PackedChunks` with heading boundaries (unchanged). |
| Other | `PackedChunks` with no boundaries → fixed windows (unchanged). |

### 4.2 Container filtering

`DeclarationScanner` reports a boundary for every block whose declaration line is a
declaration, including the *class/struct/interface/record/enum/namespace* declaration
itself. Those are containers, not members: making them their own chunk produces tiny
header-only chunks and detaches the header from the first method. `IsContainerDeclaration`
filters them out, so the container header becomes the preamble of the first real member.

### 4.3 Oversized members

A single member larger than `MaxChunkLines` (420) cannot stay whole. It is split into
`MaxChunkLines`-wide slices with `OverlapLines` (30) of tail repetition — the only
case where chunks are not a partition of the file. This preserves the existing
`Chunk_MethodLargerThanTheCap_*` behavior.

### 4.4 Constants

`TargetLines`, `MinChunkLines`, `HeadroomLines` now apply only to packed mode (prose
and fallback). `MaxChunkLines` and `OverlapLines` apply to both modes.

## 5. Implementation

- `src/backend/Eling.Core/Codebase/CodebaseChunker.cs`
  - `Chunk` branches on brace language; uses `DeclarationScanner.FindMemberBoundaries`,
    filters containers, calls `MemberChunks` or falls back to `PackedChunks`.
  - New `MemberChunks` (one chunk per member, preamble on first, oversized split).
  - Renamed former inline loop to `PackedChunks` (unchanged logic).
  - New `IsContainerDeclaration` helper.
- `tests/Eling.Core.Tests/CodebaseChunkerTests.cs`
  - `Chunk_ContentShorterThanOneChunk_ReturnsASingleChunk` → renamed to
    `Chunk_SingleMethodFile_ReturnsASingleChunk` (single-method fixture).
  - Added `Chunk_MultiMethodFile_ReturnsOneChunkPerMethod`.

## 6. Testing

- `dotnet test tests/Eling.Core.Tests --filter CodebaseChunker` → 10 passed.
- `dotnet test tests/Eling.Core.Tests --filter DeclarationScanner` → 10 passed.
- `dotnet test tests/Eling.Backend.Tests --filter Codebase` → 113 passed.

## 7. Migration / Ops

Existing `codebase.db` files hold chunks produced by the old packing strategy. They
are not migrated automatically; run `codebase_index(full=true)` on each workspace to
rebuild with per-method chunks. The debounced watcher only re-chunks files that change.
No schema or API change is required.
