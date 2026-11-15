# codebase_search: `match` parameter — short design

Date: 2026-11-15 · Status: proposed · Size: small

## Intent

`codebase_search` is global by default (`scope=all`, every indexed workspace).
The only narrowing is `pathPrefix`/`filePattern` inside a content FTS query, so
"which file is X in" forces reading content hits from every workspace. The
dashboard already solved this with a three-way match selector
(`search=content|path|file`). This change brings the same capability to the
MCP tool, and to the in-process agent tool by parity.

## Decisions (made in conversation, 2026-11-15)

- One enum parameter `match`: `content` (default) | `path` | `file`.
  Mirrors the dashboard's `search` semantics 1:1; `match` chosen over
  `search` because it reads unambiguously next to `query`.
- Response is additive: `CodebaseSearchResponse` gains `Mode`
  (`"content"` default | `"files"`) and `Files`
  (`IReadOnlyList<CodebaseSearchFile>?`, default `null`). In files mode
  `Hits` is empty and `Files` carries the rows. No existing field changes
  meaning; older JSON consumers see one extra field.
- `CodebaseSearchAgentTool` (chat) gains the same parameter, with schema and
  description updated to match.

## Differences from the dashboard (deliberate or preexisting)

The `match` selector is the same three modes on the same Core code, but the
tool is not a clone of the REST surface:

- Empty query: the dashboard shows the recent-files listing (`mode="top"`);
  the tool keeps requiring a query and returns "Query is empty" — a tool is
  for answering, not browsing.
- Envelope: the dashboard wraps in the API envelope
  (`{ code, message, data: { mode, results, stats } }`); the tool returns
  `CodebaseSearchResponse` (with `Mode`/`Files` added here).
- Parameter name: `search` on the endpoint (it sits in `?search=`), `match`
  on the tool (reads unambiguously next to `query`).
- `filePattern`: the tool's content mode already passed `filePattern` to the
  FTS; the dashboard's content mode passes `pathPrefix` only. Unchanged.
- Root list for `scope=all`: the tool drops registered roots whose index
  file does not exist yet; the endpoint keeps them and lets the per-root
  read skip a missing DB. Unchanged.
- `projectFilter` (workspace folder-name substring) is a tool-only feature;
  the dashboard scopes with repeated `project=` params or `scope=all`.
- `limit`: dashboard 1–20000, tool 1–50 (both clamp). Unchanged.
- Errors: the dashboard answers HTTP 400; the tool has no status codes and
  returns the reason as `hint`, as it already does for scope validation.

## Semantics

- `match=content` (absent/default): behaviour unchanged — FTS over contents,
  `pathPrefix`/`filePattern` apply. `Mode="content"`, `Files=null`.
- `match=path`: query matched against the workspace-relative path, folders
  included (`src/backend` matches every file under it).
- `match=file`: query matched against the file name only; folder names are
  ignored.
- Files mode answers with file rows: `ProjectRoot`, `Path`, `ChunkCount`,
  `LastIndexedAt`. No line ranges, no snippet — file rows have none.
- Roots resolution is identical to content mode: explicit `projects[]`
  (validated by `EnsureCodebaseRootsAllowed`) overrides; `scope=project`
  reads this workspace only; otherwise global with `projectFilter` applied,
  local root first. `SearchFilesWithTotalsAsync` is reused as-is.
- `pathPrefix`/`filePattern` apply to content match only (the Core
  file-search takes no such filters) and are documented as ignored in files
  mode.
- Unknown `match` value → the existing ArgumentException path: a response
  with `TotalHits=0` and a hint naming the valid values (a tool has no
  status code; this mirrors how scope validation failures surface).
- `Stats.TotalHits` in files mode = sum of per-root complete match counts
  (`SearchFilesWithTotalsAsync` totals), so the total does not move with the
  page size — same semantics as the dashboard's `totalHits`.
- `Fresh` derived from index presence on disk, as today.
- Empty query: unchanged — the tool returns "Query is empty" and records
  nothing. The dashboard's recent-files listing is not ported.
- Audit: same `codebase_search` access entry, hit count = file count.

## Surfaces kept in alignment

- `CodebaseSearchTool`: class doc comment, `[Description]` gains one clause
  for `match`.
- `ServerInstructions.cs` "Codebase:" section: "narrowed with
  pathPrefix/filePattern" gains the file/path alternative.
- `CodebaseSearchAgentTool`: ParametersJsonSchema and Description gain `match`
  (enum content|path|file).
- New DTO `CodebaseSearchFile`; `CodebaseSearchResponse` and
  `CodebaseSearchStats` unchanged otherwise. Positional-record defaults keep
  existing constructors (and tests) compiling.
- `ToolCatalog` description stays as-is (still accurate).

## Tests

Core file-search is already covered by `CodebaseFileSearchTests`. New
tool-level tests (pattern: direct `new CodebaseSearchTool(svc, ...)` over a
temp workspace): `match=file` finds by name, `match=path` finds by folder,
`match=file` ignores folder names, default stays content, unknown `match`
returns the hint, files mode returns `Mode="files"` with `ChunkCount` on
rows, audit entry recorded with file count.

## Non-goals

No dashboard changes (the feature exists there). No recent-files/top mode in
the tool. No synthesized line ranges or snippets for file rows. Multiple
needles / or-fallback for the match mode is the user's deferred idea,
tracked in memory — not part of this design.
