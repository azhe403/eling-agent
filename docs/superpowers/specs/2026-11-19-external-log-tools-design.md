# External log file tools — design (short)

Date: 2026-11-19
Status: draft, awaiting approval

## Intent / why

`log_read`/`log_list` only see Eling's own rolling logs in the central log directory.
The user needs the same convenience for **arbitrary external log files and folders**
(e.g. logs of another application on this machine): list what is there, then read
one file, with the same tail/level/contains filtering and the same transport-safety
caps. This must not change the behavior of the existing Eling log tools.

## Goals

- `log_list_dir`: list files in a caller-supplied folder, with name, size, last-write
  time and full path, so the agent can pick a file and pass it straight to
  `log_read_file`.
- `log_read_file`: read a single caller-supplied file, tailed and filtered exactly
  like `log_read` (level/contains/tail, NUL-hole sanitize, byte caps).

## Non-goals / anti-goals

- No write/modify of any file; both tools are strictly read-only.
- No glob recursion surprises: `recursive` is explicit, default `false`.
- No multi-file merge, no timestamp sorting across files (single-file and directory
  listing semantics only).
- No change to `log_read`, `log_list`, `log.json` handling, or the dashboard REST
  endpoints.

## Approaches considered

1. **Extend `log_read`/`log_list` with an external `path` mode** (rejected): fewest
   lines, but every existing parameter becomes contextual (stream/date vs path), the
   description gets longer, and the mode boundary is implicit. User explicitly
   rejected this for `log_read`.
2. **One dual-mode tool** (folder → list, file → read) (rejected): one-call
   convenience, but a two-meaning contract with different response shapes per branch.
3. **Two separate tools in `LogTools.cs`** (chosen): mirrors the existing
   `log_list`/`log_read` pair, self-describing schemas, `ToolCatalog` gains two
   `diagnostics` entries instead of one overloaded one.

## The two tools

Both: read-only, never throw for missing paths (typed `not_found` / clear error JSON),
work with absolute paths or paths relative to the workspace, group `diagnostics`,
auto-discovered like the other MCP tools, added by hand to `ToolCatalog` (convention
test `AllTools_BelongToKnownGroups` requires it).

### `log_list_dir`

Parameters:

- `path` (string, required): folder to list. Missing → `not_found`; exists but is a
  file → clear error; empty/whitespace → error.
- `pattern` (string, required): glob matched against file **names** (e.g. `*.log`,
  `*.jsonl`, `*` for everything). Not validated beyond non-empty; case-insensitive on
  Windows semantics (use `MatchCasing.Platform`-equivalent behavior — .NET
  `FileSystemName.MatchesSimpleExpression` is acceptable).
- `recursive` (bool, default `false`): recurse into subdirectories.
- Result cap: newest 200 entries by last-write time; response notes the cap.

Response: `{ directory, pattern, recursive, count, capped, files: [{ name, fullPath,
sizeBytes, lastWriteTimeUtc }] }`, sorted by last-write time descending. Symlinks are
not specially followed. `count` = returned entries (post-cap); `capped` = true when
more matched.

### `log_read_file`

Parameters:

- `path` (string, required): the file to read. Missing → `not_found`; exists but is a
  directory → clear error; empty/whitespace → error.
- `tail` (int, default 200, clamped 1–5000).
- `level` (string, optional), `contains` (string, optional): same semantics as `log_read`.
- `format` (string, optional): `text` / `jsonl` / `auto` (default `auto`): `.jsonl`
  (case-insensitive) → jsonl, everything else → text. The configured `log.json`
  format is intentionally **not** consulted.

Response: identical envelope to `log_read` — `{ file, directory, totalLines,
filteredTotal, returned, lines, format, files }` where `directory` = parent directory,
`file` = file name, `files` = `[file]`. `too_large` / `file-not-found` branches keep
the same shape as `log_read`.

## Core / implementation notes (calls for the builder)

- `LogReader` gains one public method `ReadFile(string path, string? level, string?
  contains, int tail, LogFormat format)`: clamps `tail`, returns a `LogReadResult`
  built from a single `ReadFilteredTail` segment (no `Segments()`, no inter-file
  merge, no global byte re-pass — the per-segment window cap already bounds bytes).
- Folder listing lives in Core as well (`LogReader.ListDirectory(directory, pattern,
  recursive, cap)` or a sibling helper) so the MCP tool and any future REST endpoint
  share it; it must not touch the existing `ListFiles` Eling-dir method.
- The `MaxResponseBytes` envelope check and `too_large` fallback are extracted into a
  private helper shared by `log_read` and `log_read_file` — no copy-paste of the
  serialization guard.
- `ToolCatalog` + any group-count convention tests updated for the two new tools.
- Tests: `LogReader.ReadFile` (missing file → not-found result; directory path;
  level/contains filter; tail clamp; jsonl vs text auto), `ListDirectory` (pattern
  required behavior is at tool layer; glob match, size/lastWrite, newest-first cap,
  recursive on/off, empty folder), tool-level envelope tests if a LogTools test file
  exists.

## Constraints

- Response bound: same 8 MiB tool-serialization guard as `log_read`; transport limit
  16 MiB untouched.
- Never-throws contract (missing paths are typed results, not exceptions).
- Convention tests in `Eling.Backend.Tests` must stay green (ToolCatalog, no-new
  silent catch, etc.).

## What's left to the builder

Exact parameter naming/casing in the tool schema (keep camelCase descriptions
consistent with existing tools), glob implementation choice, and whether
`log_list_dir`'s cap is 200 or reuses an existing constant.
