# Filesystem MCP Tools — Design

**Date:** 2026-09-04
**Status:** Implemented (2026-09-11) — the six core tools from this spec plus nine extensions; see §9 for the full 15-tool inventory.
**Scope:** Eling backend (`Eling.Backend`, `Eling.Core`) + new tests in `Eling.Backend.Tests`

---

## 1. Goal

The original goal was six simple MCP tools so an agent can test, create, list, glob, read, and write filesystem paths under the project root:

1. `path_test` — check whether a path exists and report its kind (file / directory / symlink) and size.
2. `directory_create` — make a directory (and any missing parents), idempotent.
3. `directory_list` — list the contents of a directory, optionally recursive with a depth cap and a glob pattern.
4. `glob` — search for files/directories matching a glob pattern under a given path, with a result cap.
5. `file_read` — read a text file (with a byte cap, a binary detector, and optional line pagination).
6. `file_write` — write a text file, creating parent directories on demand.

The shipped surface grew to fifteen tools: the six above plus `file_delete`, `directory_delete`, `file_move`, `directory_move`, `file_copy`, `directory_copy`, `file_edit`, `file_append`, and `file_search` (full inventory in §9, contracts in §5).

These tools mirror the shape of the existing `memory_*` tools: a thin MCP wrapper that calls an `IFileSystemService` from `Eling.Core`, with all path resolution and sandboxing implemented inside the service so it can be unit-tested with a fake. They are not a general-purpose filesystem API — they are scoped to the project root, the read/edit/append/search tools reject binary files, and every operation caps sizes and recursion so an agent cannot accidentally fill a context window or a disk.

The drivers are:

1. **Symmetry with `memory_*`.** The agent already has a `memory_save`/`memory_get` workflow; basic file operations are the obvious next step.
2. **Testability without real disk.** All file logic is behind `IFileSystemService`, so unit tests run against an in-memory fake, not a temp directory on the build host.
3. **Bounded blast radius.** Every operation is sandboxed under `ProjectScope.Root`; binary detection, size caps, and safe defaults on destructive operations prevent accidental context-window or disk exhaustion.

The non-goals are:

- File metadata scripting (`chmod`, `chown`). Destructive or platform-specific; deferred. (`delete`, `move`, `copy`, and recursive delete were originally listed here but are now implemented.)
- Multi-root access (e.g. `$HOME`, `%APPDATA%`). Out of scope; the only allowed root is `ProjectScope.Root` at this time.
- File watching, tailing, or streaming reads. `file_read` returns the whole file (or a line slice) in one call.
- Symlink creation or symlink-target resolution that crosses the sandbox boundary. The service resolves the input path; it does not follow symlinks whose targets land outside the root.

---

## 2. Design summary

A new `IFileSystemService` lives in `Eling.Core.FileSystem` with fifteen methods matching the fifteen tools. The default implementation `FileSystemService` lives in `Eling.Backend.FileSystem` and uses `System.IO` (`File`, `Directory`, `Path`) plus a `rootPath` injected from `ProjectScope.Root`. A new `FileSystemTools` class in `Eling.Backend/Mcp/Tools/` is the thin MCP wrapper and is auto-discovered by `WithToolsFromAssembly()`.

`FileSystemService` accepts the root path through its constructor (not via `ProjectScope` directly) so the unit test can pass a sandbox directory. In production, both `AddElingCoreServices` overloads register the service: the path-based overload uses `projectScope.Root`, and the scope-chain overload uses `chain.Head?.Root ?? chain.Cwd`. The service does its own `Path.GetFullPath` resolution and rejects any input whose resolved path is not under the root — this is the only sandbox enforcement, and it is unit-tested.

The tools return JSON-serialised results (one object per call), except `file_read` without `offset`/`limit`, which returns the raw file text; a paginated `file_read` returns a JSON envelope. Errors the agent can act on (sandbox violation, not found, binary detected, would overwrite or delete without permission) come back as `{ "ok": false, "error": "...", "code": "..." }` so the agent can branch on `code`; unexpected exceptions are caught at the tool boundary and returned as `internal_error`.

A `FakeFileSystemService` is added to `tests/Eling.Backend.Tests/FileSystem/` mirroring `FakeMemoryService` so `FileSystemTools` can be unit-tested without touching disk. The fake is an in-memory dictionary of path → entry carrying a kind, file content, a binary flag, and an optional size override; it re-implements the same sandbox rule as the real service.

---

## 3. Components

### 3.1 `IFileSystemService` (Core)

Location: `src/backend/Eling.Core/FileSystem/IFileSystemService.cs`

```csharp
namespace Eling.Core.FileSystem;

public interface IFileSystemService
{
    PathInfo TestPath(string path);
    DirectoryCreateResult CreateDirectory(string path);
    IReadOnlyList<DirectoryEntry> ListDirectory(string path, bool recursive = false, int maxDepth = 3, string? pattern = null);
    GlobResult Glob(string basePath, string pattern, int maxDepth = 5, int maxResults = 200);
    FileReadResult ReadFile(string path, int maxBytes = 1048576, int offset = 1, int limit = 0);
    FileWriteResult WriteFile(string path, string content, bool overwrite = false);
    DeleteResult DeleteFile(string path);
    DeleteResult DeleteDirectory(string path, bool recursive = false);
    MoveResult MoveFile(string sourcePath, string destinationPath, bool overwrite = false);
    MoveResult MoveDirectory(string sourcePath, string destinationPath, bool overwrite = false);
    CopyResult CopyFile(string sourcePath, string destinationPath, bool overwrite = false);
    CopyResult CopyDirectory(string sourcePath, string destinationPath, bool overwrite = false);
    FileEditResult EditFile(string path, string oldString, string newString, bool replaceAll = false, int maxBytes = 1048576);
    FileAppendResult AppendFile(string path, string content);
    ContentSearchResult SearchFiles(string basePath, string pattern, bool useRegex = false, bool caseSensitive = false, string? filePattern = null, int maxDepth = 5, int maxResults = 100, int maxFileBytes = 1048576);
}
```

Result types are records in `Eling.Core.FileSystem`, one type per file. They use PascalCase property names; the MCP wrapper converts to camelCase JSON at the edge.

```csharp
public enum PathKind { NotFound, File, Directory, Symlink, Other }

public sealed record PathInfo(string ResolvedPath, bool Exists, PathKind Kind, long? SizeBytes);
public sealed record DirectoryCreateResult(string ResolvedPath, bool Created);
public sealed record DirectoryEntry(string Path, string Name, PathKind Kind, long? SizeBytes);
public sealed record FileReadResult(string ResolvedPath, long SizeBytes, string Content, int StartLine, int EndLine, int TotalLines, bool Truncated);
public sealed record FileWriteResult(string ResolvedPath, long SizeBytes, bool Overwrote);
public sealed record GlobResult(string ResolvedBasePath, string Pattern, int MatchCount, bool Truncated, IReadOnlyList<DirectoryEntry> Matches);
public sealed record DeleteResult(string ResolvedPath, bool Deleted);
public sealed record MoveResult(string SourcePath, string ResolvedPath, bool Overwrote);
public sealed record CopyResult(string SourcePath, string ResolvedPath, bool Overwrote, int EntriesCopied);
public sealed record FileEditResult(string ResolvedPath, long SizeBytes, int Replacements);
public sealed record FileAppendResult(string ResolvedPath, long SizeBytes, bool Created);
public sealed record ContentMatch(string Path, int LineNumber, string LineText);
public sealed record ContentSearchResult(string ResolvedBasePath, string Pattern, int MatchCount, bool Truncated, IReadOnlyList<ContentMatch> Matches);
```

Field semantics: `Created` is false when target already existed; `Overwrote` is true when a destination existed and was replaced; `EntriesCopied` is 1 for a file copy and the number of files for a directory copy; `FileReadResult` line fields are populated on every read (whole-file reads report the full span with `Truncated = false`); `Replacements` is the number of edits applied.

Exception types live in `src/backend/Eling.Core/Exceptions/` under `Eling.Core.Exceptions`: `PathSandboxException`, `PathAlreadyExistsException`, `FileAlreadyExistsException`, `BinaryFileException`, `FileTooLargeException`, `NotADirectoryException`, `NotAFileException`, `DirectoryNotEmptyException`, `OldStringNotFoundException`, and `AmbiguousMatchException`. The MCP wrapper catches them and maps each to a `code` (see §6).

### 3.2 `FileSystemService` (Backend)

Location: `src/backend/Eling.Backend/FileSystem/FileSystemService.cs`

```csharp
namespace Eling.Backend.FileSystem;

public sealed class FileSystemService : IFileSystemService
{
    public FileSystemService(
        string rootPath,
        int defaultReadMaxBytes = 1048576,
        int defaultMaxDepth = 3,
        ILogger<FileSystemService>? logger = null)
    { /* ... */ }
}
```

The service is registered in both `McpServiceExtensions.AddElingCoreServices` overloads, each with the root from the discovered scope:

```csharp
services.TryAddSingleton<IFileSystemService>(new FileSystemService(projectScope.Root));

// scope-chain overload
services.TryAddSingleton<IFileSystemService>(
    new FileSystemService(chain.Head?.Root ?? chain.Cwd));
```

### 3.3 `FileSystemTools` (Backend MCP wrapper)

Location: `src/backend/Eling.Backend/Mcp/Tools/FileSystemTools.cs`

The class is annotated `[McpServerToolType]` and each of its fifteen methods is `[McpServerTool(Name = "...", Description = "...")]`. The wrapper is short on purpose: it clamps caller-controlled limits, calls the service, and serialises the result. Predictable failures become `{ ok: false, error, code }` JSON; the agent gets a structured failure it can branch on.

```csharp
[McpServerToolType]
public sealed class FileSystemTools
{
    private readonly IFileSystemService _fs;
    private readonly ILogger<FileSystemTools>? _log;

    public FileSystemTools(IFileSystemService fs, ILogger<FileSystemTools>? logger = null)
    { /* ... */ }

    [McpServerTool(Name = "path_test"), Description("...")]
    public string PathTest(
        [Description("Absolute or project-relative path.")] string path)
    { /* ... */ }
    // ... fourteen more methods, all the same shape
}
```

`WithToolsFromAssembly()` in `McpServiceExtensions` picks the class up automatically — no per-tool registration needed.

### 3.4 `FakeFileSystemService` (Tests)

Location: `tests/Eling.Backend.Tests/FileSystem/FakeFileSystemService.cs`

Mirrors `FakeMemoryService` from the memory tests. Backed by a `Dictionary<string, FakeEntry>` keyed by a normalised full path; `FakeEntry` carries a `Kind`, file `Content`, an `IsBinary` flag, and a `SizeOverride` used to simulate oversized files. The fake re-implements the same sandbox rule as the real service: relative inputs resolve under the root, and anything outside throws `PathSandboxException`.

---

## 4. Sandbox and path resolution

### 4.1 Resolution rule

Every method on `IFileSystemService` runs the input through this sequence:

1. Reject null / empty / whitespace input with `ArgumentException`.
2. Compute `full = Path.GetFullPath(Path.Combine(_rootPath, input))` so relative inputs land under the root and absolute inputs are still normalised.
3. Compute `rootWithSep = _rootPath` with a trailing separator appended.
4. If `full` is neither under `rootWithSep` nor equal to the root itself, throw `PathSandboxException`.
5. Return `full` as `ResolvedPath` on the result record.

The trailing separator is the standard pattern for preventing the `/foo-bar` vs `/foo` confusion: if the root is `C:\some-folder\Eling` then the comparison string is `C:\some-folder\Eling\`, and `C:\some-folder\ElingOther` is correctly rejected. The root itself is allowed so that `directory_list` / `glob` may address the sandbox root.

The platform comparison is selected at construction time (one branch, not per call):

```csharp
_rootComparison = OperatingSystem.IsWindows()
    ? StringComparison.OrdinalIgnoreCase
    : StringComparison.Ordinal;
```

### 4.2 Symlinks

The service does **not** follow symlinks during sandbox resolution. The rule is: if the input path is a symlink, the resolved path of the link itself is what we check against the sandbox. We do not chase it to a target outside the root. This is the safe default and matches the principle that an agent should be able to reason about what it is touching without surprise indirection.

For `path_test`, the service reports `Kind = Symlink` when `FileInfo.LinkTarget` (or `DirectoryInfo.LinkTarget`) is non-null; the link target itself is not inspected. `directory_list` does not follow symlinks into other directories — symlinks to directories are listed with `Kind = Symlink`, not expanded — and `directory_copy` skips symlinks entirely during traversal.

### 4.3 Defaults and limits

| Setting | Default | Where set | Notes |
|---|---|---|---|
| Read byte cap | 1 MiB (1_048_576) | `FileSystemService` ctor | Reject larger files; do not stream. Applies even when `offset`/`limit` are supplied. |
| Read line clamp | `offset >= 1`, `limit` `[0, 5000]` | MCP wrapper | `limit = 0` reads to end of file. |
| Recursive depth cap (list) | 3 | `FileSystemService` ctor | Wrapper clamps to `[0, 5]`; a negative `maxDepth` falls back to this service default. |
| Recursive depth cap (glob) | 5 | per call | Wrapper clamps to `[0, 10]`. |
| Glob result cap | 200 | per call | Wrapper clamps to `[1, 1000]`. |
| Glob pattern | none (all entries) | per call | `*`, `?`, `[abc]` match entry names; `**` crosses directory boundaries; a pattern without `**` matches trailing path segments at any depth. |
| Search result cap | 100 | per call | `file_search`; wrapper clamps to `[1, 1000]`. |
| Search file-byte cap | 1 MiB (1_048_576) | per call | `file_search`; wrapper clamps to `[1024, 5242880]`; larger files are skipped. |
| Edit byte cap | 1 MiB (1_048_576) | per call | `file_edit`; wrapper clamps to `[1, 1048576]`. |
| Overwrite (`file_write`, `file_move`, `directory_move`, `file_copy`, `directory_copy`) | `false` | per call | When `false` and the destination exists, return `already_exists`; do not write. |
| Recursive (`directory_delete`) | `false` | per call | A non-empty directory requires `recursive: true`, otherwise `directory_not_empty`. |

The MCP wrapper clamps `maxDepth` to `[0, 5]` (list) / `[0, 10]` (glob and search), `maxResults` to `[1, 1000]`, `maxFileBytes` to `[1024, 5242880]`, and the read/edit byte caps to `[1, 1048576]` before calling the service, so a misbehaving caller cannot ask for unlimited recursion or a million results. The service itself accepts any non-negative integer; the clamp is at the trust boundary.

---

## 5. Tool contracts

### 5.1 `path_test`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Absolute or project-relative path. |

Returns JSON:

```json
{
  "resolvedPath": "C:\\some-folder\\Eling\\src\\backend\\Eling.Backend\\Eling.Backend.csproj",
  "exists": true,
  "kind": "file",
  "sizeBytes": 4096
}
```

When `exists` is `false`, `kind` is `notFound` and `sizeBytes` is `null`. A not-found path is reported as data, not an error; the call only throws for a sandbox violation or an invalid (empty) path.

### 5.2 `directory_create`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Absolute or project-relative directory path. |

Returns JSON:

```json
{ "resolvedPath": "C:\\some-folder\\Eling\\src\\backend\\Eling.Backend\\Foo\\Bar", "created": true }
```

`created` is `false` when the directory already existed. If any parent is a file (not a directory), throws `DirectoryNotFoundException`-equivalent, which the wrapper turns into a JSON error. Equivalent to `mkdir -p` plus idempotency.

### 5.3 `directory_list`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `path` | string | yes | — | Directory to list. |
| `recursive` | bool | no | `false` | Recurse into subdirectories. |
| `maxDepth` | int | no | `3` | Depth cap; wrapper clamps to `[0, 5]`. |
| `pattern` | string | no | `null` | Glob like `*.cs` applied to entry names (files and directories). |

Returns JSON array of `DirectoryEntry` objects:

```json
[
  { "path": "C:\\some-folder\\Eling\\src", "name": "src", "kind": "directory", "sizeBytes": null },
  { "path": "C:\\some-folder\\Eling\\README.md", "name": "README.md", "kind": "file", "sizeBytes": 4096 }
]
```

Entries are sorted: directories first, then files, each group alphabetical (case-insensitive on Windows, ordinal elsewhere). When `recursive` is `true`, results are flat (not nested) — each entry carries its full path so the agent can rebuild a tree. Symlinks are listed with `Kind = Symlink` and never expanded.

### 5.4 `glob`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `basePath` | string | yes | — | Absolute or project-relative root to search under. |
| `pattern` | string | yes | — | Glob pattern applied to entry names. Supports `*`, `?`, `**` (recursive) and `[abc]` character classes. |
| `maxDepth` | int | no | `5` | Depth cap; wrapper clamps to `[0, 10]`. |
| `maxResults` | int | no | `200` | Hard cap on returned entries. Wrapper clamps to `[1, 1000]`. |

Returns JSON:

```json
{
  "resolvedBasePath": "C:\\some-folder\\Eling\\src",
  "pattern": "**/*.cs",
  "matchCount": 137,
  "truncated": false,
  "matches": [
    { "path": "C:\\some-folder\\Eling\\Program.cs", "name": "Program.cs", "kind": "file", "sizeBytes": 4096 },
    { "path": "C:\\some-folder\\Eling\\Memory", "name": "Memory", "kind": "directory", "sizeBytes": null }
  ]
}
```

`glob` differs from `directory_list` in three ways:

1. **Pattern always recursive** — `*` matches entries at any depth; `**` matches across directory boundaries. The agent supplies one pattern, not a directory + filter.
2. **Match against directories too** — `directory_list` returns whatever is in a directory; `glob` returns any entry (file *or* directory) whose final path segment matches the pattern. Useful for finding "every `Controllers` directory" or "every `*.csproj` file".
3. **Result cap + truncation flag** — a glob like `**/*` over a large tree can return thousands of entries. The service stops after `maxResults` and sets `truncated: true` so the agent can decide to narrow the pattern.

The `basePath` is sandboxed under `ProjectScope.Root` exactly like every other method. `pattern` is **not** a path — it is matched against entry names only, so `../etc` in a pattern is harmless (it just won't match anything) and the service does not resolve it.

Entries are sorted by path (case-insensitive on Windows, ordinal elsewhere). Symlinks are listed with `Kind = Symlink` and never expanded. Hidden entries (those starting with `.` on Linux/macOS, and Windows hidden attribute when the runtime exposes it) are **included by default** — the agent can prefix the pattern with `.` explicitly when it wants dotfiles.

### 5.5 `file_read`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `path` | string | yes | — | Absolute or project-relative file path. |
| `maxBytes` | int | no | `1_048_576` | Byte cap; wrapper clamps to `[1, 1048576]`. Files larger than the cap are rejected even when `offset`/`limit` are supplied. |
| `offset` | int | no | `1` | First line to return (1-based). |
| `limit` | int | no | `0` | Max lines to return; `0` = to end of file. Wrapper clamps to `[0, 5000]`. |

Without `offset`/`limit` the tool returns the plain file content (a string). With either supplied it returns a JSON envelope:

```json
{
  "resolvedPath": "C:\\some-folder\\Eling\\README.md",
  "sizeBytes": 4096,
  "content": "line 2\nline 3",
  "startLine": 2,
  "endLine": 3,
  "totalLines": 120,
  "truncated": true
}
```

`truncated` is true when lines remain after `endLine`. An `offset` past the end returns empty content, `endLine` equal to `startLine - 1`, and `truncated: false`. Slices normalise CRLF to LF and count lines without a trailing empty line.

Binary detection: read the first 8 KiB (8192 bytes); if it contains a NUL byte (`0x00`), treat as binary and throw `BinaryFileException` (which the wrapper turns into JSON error). Encoding: UTF-8 with BOM detection — if the file starts with a UTF-8 BOM (`EF BB BF`), strip it from the returned string so the agent does not see a stray `\ufeff`. If the file is bigger than `maxBytes`, throw `FileTooLargeException`.

### 5.6 `file_write`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `path` | string | yes | — | Absolute or project-relative file path. |
| `content` | string | yes | — | File content (UTF-8). |
| `overwrite` | bool | no | `false` | When `false` and the file exists, return error without writing. |

Returns JSON:

```json
{ "resolvedPath": "C:\\some-folder\\Eling\\Foo.txt", "sizeBytes": 42, "overwrote": false }
```

Parent directories are created on demand. Encoding is UTF-8 without BOM (so `overwrite: true` does not silently change encoding).

### 5.7 `file_delete`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Absolute or project-relative file path. |

Returns JSON:

```json
{ "resolvedPath": "C:\\some-folder\\Eling\\old.txt", "deleted": true }
```

Deletes a file only. A directory path returns `not_a_file`; a missing path returns `not_found`; deleting the sandbox root returns `invalid_argument`. A file symlink is deleted as the link itself (its target is never touched); a directory symlink returns `not_a_file`.

### 5.8 `directory_delete`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `path` | string | yes | — | Absolute or project-relative directory path. |
| `recursive` | bool | no | `false` | Delete a non-empty directory and everything under it. |

Returns JSON `{ "resolvedPath": "...", "deleted": true }`.

A non-empty directory without `recursive: true` returns `directory_not_empty` and leaves the tree intact. A missing directory returns `not_found`; a file path returns `not_a_directory`; deleting the sandbox root returns `invalid_argument`. Symlinks encountered during recursion are removed as links and never followed outside the root.

### 5.9 `file_move`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `sourcePath` | string | yes | — | Absolute or project-relative source file path. |
| `destinationPath` | string | yes | — | Absolute or project-relative destination file path. |
| `overwrite` | bool | no | `false` | Replace the destination when it already exists. |

Returns JSON:

```json
{ "sourcePath": "C:\\...\\a.txt", "resolvedPath": "C:\\...\\sub\\b.txt", "overwrote": false }
```

Destination parents are created on demand. A missing source returns `not_found`; a directory source returns `not_a_file`; an existing destination without `overwrite` returns `already_exists`; identical source and destination, or moving the sandbox root, returns `invalid_argument`.

### 5.10 `directory_move`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `sourcePath` | string | yes | — | Absolute or project-relative source directory path. |
| `destinationPath` | string | yes | — | Absolute or project-relative destination directory path. |
| `overwrite` | bool | no | `false` | Replace the destination when it already exists. |

Returns JSON `{ "sourcePath": "...", "resolvedPath": "...", "overwrote": false }`.

Destination parents are created on demand. A file source returns `not_a_directory`; a missing source returns `not_found`; moving a directory inside itself returns `invalid_argument`; an existing destination without `overwrite` returns `already_exists` (on overwrite the destination is removed first, then the source is moved).

### 5.11 `file_copy`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `sourcePath` | string | yes | — | Absolute or project-relative source file path. |
| `destinationPath` | string | yes | — | Absolute or project-relative destination file path. |
| `overwrite` | bool | no | `false` | Replace the destination when it already exists. |

Returns JSON:

```json
{ "sourcePath": "C:\\...\\a.txt", "resolvedPath": "C:\\...\\sub\\a.txt", "overwrote": false, "entriesCopied": 1 }
```

The source is left unchanged, and its content (including a binary source) is copied verbatim. Errors mirror `file_move`: `not_found`, `not_a_file`, `already_exists`, `invalid_argument`, `sandbox_violation`.

### 5.12 `directory_copy`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `sourcePath` | string | yes | — | Absolute or project-relative source directory path. |
| `destinationPath` | string | yes | — | Absolute or project-relative destination directory path. |
| `overwrite` | bool | no | `false` | Replace the destination when it already exists. |

Returns JSON:

```json
{ "sourcePath": "C:\\...\\src", "resolvedPath": "C:\\...\\dst", "overwrote": false, "entriesCopied": 2 }
```

`entriesCopied` counts the files copied. The source tree is left unchanged. Destination parents are created on demand; copying a directory inside itself returns `invalid_argument`; an existing destination without `overwrite` returns `already_exists` (on overwrite the destination is removed first). Symlinks are skipped during traversal, so a copy never follows a link outside the root.

### 5.13 `file_edit`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `path` | string | yes | — | Absolute or project-relative file path. |
| `oldString` | string | yes | — | Exact text to find; must match exactly once unless `replaceAll` is true. |
| `newString` | string | yes | — | Replacement text. |
| `replaceAll` | bool | no | `false` | Replace every occurrence instead of requiring a unique match. |
| `maxBytes` | int | no | `1_048_576` | Byte cap; wrapper clamps to `[1, 1048576]`. |

Returns JSON:

```json
{ "resolvedPath": "C:\\...\\code.txt", "sizeBytes": 42, "replacements": 1 }
```

Matching tolerates CRLF/LF differences two ways: an LF `oldString` matches a CRLF file and vice versa, and the replacement adopts the file's existing line ending. Failures: `old_string_not_found` (no match), `multiple_matches` (more than one match without `replaceAll`), `binary_file`, `file_too_large`, `not_found`, `not_a_file`, `sandbox_violation`.

### 5.14 `file_append`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Absolute or project-relative file path. |
| `content` | string | yes | Text to append (as-is; no newline is added). |

Returns JSON:

```json
{ "resolvedPath": "C:\\...\\log.txt", "sizeBytes": 128, "created": true }
```

Creates the file and any missing parents when absent (`created: true`), otherwise appends (`created: false`). Refuses a binary file (`binary_file`); a directory path returns `not_a_file`; appending to the sandbox root returns `invalid_argument`.

### 5.15 `file_search`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `basePath` | string | yes | — | Absolute or project-relative root directory to search under. |
| `pattern` | string | yes | — | Substring, or a .NET regex when `useRegex` is true. |
| `useRegex` | bool | no | `false` | Treat `pattern` as a regex (2 s timeout per line). |
| `caseSensitive` | bool | no | `false` | Case-sensitive matching. |
| `filePattern` | string | no | `null` | Name filter like `*.cs`. |
| `maxDepth` | int | no | `5` | Depth cap; wrapper clamps to `[0, 10]`. |
| `maxResults` | int | no | `100` | Max hits; wrapper clamps to `[1, 1000]`. |
| `maxFileBytes` | int | no | `1_048_576` | Files larger than this are skipped; wrapper clamps to `[1024, 5242880]`. |

Returns JSON:

```json
{
  "resolvedBasePath": "C:\\some-folder\\Eling\\src",
  "pattern": "sandbox",
  "matchCount": 2,
  "truncated": false,
  "matches": [
    { "path": "C:\\...\\FileSystemService.cs", "lineNumber": 42, "lineText": "// sandbox root" }
  ]
}
```

Literal search is case-insensitive by default. Binary files and files over `maxFileBytes` are skipped silently; `lineText` is trimmed and truncated to 500 characters; `truncated` is set when more hits exist than `maxResults`. A `basePath` that is a file returns `not_a_directory`; an invalid regex returns `invalid_argument`.

---

## 6. Error handling

The MCP wrapper returns `{ "ok": false, "error": "...", "code": "..." }` for every predictable failure so the agent can branch on `code`. `code` is one of:

| Code | Meaning | When |
|---|---|---|
| `sandbox_violation` | Path is outside the project root. | `PathSandboxException` |
| `not_found` | The path does not exist. | `FileNotFoundException` / `DirectoryNotFoundException` |
| `already_exists` | `file_write`, `file_move`, `directory_move`, `file_copy`, or `directory_copy` with `overwrite=false` and the destination present. | `PathAlreadyExistsException` (and its `FileAlreadyExistsException` subclass) |
| `directory_not_empty` | `directory_delete` without `recursive=true` on a non-empty directory. | `DirectoryNotEmptyException` |
| `binary_file` | A text-only operation (`file_read`, `file_edit`, `file_append`, `file_search`) on a file with NUL bytes in the first 8 KiB. | `BinaryFileException` |
| `file_too_large` | `file_read` at or above `maxBytes`. | `FileTooLargeException` |
| `not_a_directory` | A directory operation (`directory_list`, `directory_delete`, `directory_move`, `directory_copy`, `file_search`) on a file path. | `NotADirectoryException` |
| `not_a_file` | A file operation (`file_read`, `file_write`, `file_delete`, `file_move`, `file_copy`, `file_append`) on a directory path. | `NotAFileException` |
| `old_string_not_found` | `file_edit` where `oldString` matches nowhere. | `OldStringNotFoundException` |
| `multiple_matches` | `file_edit` where `oldString` matches more than once and `replaceAll=false`. | `AmbiguousMatchException` |
| `invalid_argument` | Bad parameter (null, empty, negative depth, identical source and destination, moving/copying a directory into itself, or the sandbox root used as a delete/move/copy target). | `ArgumentException` |
| `internal_error` | Anything else. | Any other exception |

For `path_test`, only `sandbox_violation`, `invalid_argument`, and `internal_error` apply — the call is read-only, so `not_found` is reported as `{ "exists": false }` rather than as an error.

Success responses are the JSON shapes shown in §5, except `file_read` without `offset`/`limit`, which returns the plain file content as a string.

---

## 7. Tests

### 7.1 Unit tests — `FileSystemToolsTests`

Location: `tests/Eling.Backend.Tests/FileSystem/FileSystemToolsTests.cs`

Uses `FakeFileSystemService` to drive each tool through the thin MCP wrapper, covering both success JSON shapes and the `{ ok: false, code }` error mapping. 59 cases, grouped by tool:

**path_test**
- `PathTest_ExistingFile_ReturnsFileInfo`
- `PathTest_NonExistent_ReturnsNotFoundShape`
- `PathTest_OutsideRoot_ReturnsSandboxError`
- `PathTest_EmptyPath_ReturnsInvalidArgument`

**directory_create**
- `DirectoryCreate_NewDirectory_ReturnsCreatedTrue`
- `DirectoryCreate_ExistingDirectory_ReturnsCreatedFalse`
- `DirectoryCreate_OutsideRoot_ReturnsSandboxError`

**directory_list**
- `DirectoryList_Flat_ReturnsDirectChildrenOnly`
- `DirectoryList_Recursive_ReturnsNestedEntries`
- `DirectoryList_PatternFilter_ReturnsMatchesOnly`
- `DirectoryList_OnFile_ReturnsNotADirectory`

**glob**
- `Glob_DoubleStarPattern_ReturnsAllDescendants`
- `Glob_TruncationFlag_SetWhenMaxResultsExceeded`
- `Glob_OutsideRoot_ReturnsSandboxError`

**file_read**
- `FileRead_ExistingTextFile_ReturnsContent`
- `FileRead_BinaryFile_ReturnsBinaryError`
- `FileRead_OversizedFile_ReturnsFileTooLargeError`
- `FileRead_MissingFile_ReturnsNotFound`
- `FileRead_WholeFile_StillReturnsPlainString`
- `FileRead_Paginated_ReturnsEnvelopeWithLineNumbers`
- `FileRead_OffsetBeyondEnd_ReturnsEmptyUntruncated`

**file_write**
- `FileWrite_NewFile_CreatesParentAndFile`
- `FileWrite_ExistingFileNoOverwrite_ReturnsAlreadyExistsError`
- `FileWrite_ExistingFileOverwrite_ReplacesContent`
- `FileWrite_SerializesWithCamelCase`

**file_delete**
- `FileDelete_ExistingFile_DeletesAndReports`
- `FileDelete_MissingFile_ReturnsNotFound`
- `FileDelete_OnDirectory_ReturnsNotAFile`
- `FileDelete_Root_ReturnsInvalidArgument`

**directory_delete**
- `DirectoryDelete_EmptyDirectory_ReturnsDeleted`
- `DirectoryDelete_NonEmptyWithoutRecursive_ReturnsDirectoryNotEmpty`
- `DirectoryDelete_Recursive_DeletesTree`

**file_move**
- `FileMove_Basic_RelocatesAndReports`
- `FileMove_ExistingDestinationNoOverwrite_ReturnsAlreadyExists`
- `FileMove_Overwrite_ReplacesDestination`

**directory_move**
- `DirectoryMove_Basic_RelocatesTree`
- `DirectoryMove_IntoItself_ReturnsInvalidArgument`
- `Move_DestinationOutsideRoot_ReturnsSandboxError`

**file_copy**
- `FileCopy_Basic_CopiesAndLeavesSource`
- `FileCopy_ExistingDestinationNoOverwrite_ReturnsAlreadyExists`
- `FileCopy_Overwrite_ReplacesDestination`

**directory_copy**
- `DirectoryCopy_Basic_CopiesTreeAndCountsFiles`
- `DirectoryCopy_ExistingDestinationNoOverwrite_ReturnsAlreadyExists`
- `DirectoryCopy_IntoItself_ReturnsInvalidArgument`
- `Copy_DestinationOutsideRoot_ReturnsSandboxError`
- `Copy_Root_ReturnsInvalidArgument`

**file_edit**
- `FileEdit_UniqueMatch_ReplacesAndReports`
- `FileEdit_NotFound_ReturnsOldStringNotFound`
- `FileEdit_AmbiguousWithoutReplaceAll_ReturnsMultipleMatches`
- `FileEdit_ReplaceAll_ReplacesEverywhere`

**file_append**
- `FileAppend_NewFile_CreatesWithContent`
- `FileAppend_ExistingFile_AppendsContent`

**file_search**
- `SearchFiles_Literal_FindsMatchingLines`
- `SearchFiles_CaseSensitive_RespectsFlag`
- `SearchFiles_Regex_MatchesPattern`
- `SearchFiles_InvalidRegex_ReturnsInvalidArgument`
- `SearchFiles_FilePattern_FiltersFiles`
- `SearchFiles_TruncationFlag_SetWhenCapped`
- `SearchFiles_BinaryFile_Skipped`

### 7.2 Sandbox tests — `FileSystemServiceTests`

Location: `tests/Eling.Backend.Tests/FileSystem/FileSystemServiceTests.cs`

Uses a real `FileSystemService` against `Path.GetTempPath()` / a per-test subdir, then cleans up. 34 cases:

**Sandbox and path resolution**
- `ResolvePath_RelativeInput_LandsUnderRoot`
- `ResolvePath_AbsoluteInputUnderRoot_Accepted`
- `ResolvePath_AbsoluteInputOutsideRoot_ThrowsPathSandboxException`
- `ResolvePath_ParentTraversal_ThrowsPathSandboxException`
- `ResolvePath_SiblingPrefixConfusion_Rejected`

**Read and write**
- `WriteThenRead_RoundTripsContent`
- `ReadFile_BinaryContent_ThrowsBinaryFileException`
- `ReadFile_OversizedContent_ThrowsFileTooLargeException`
- `ReadFile_MissingFile_ThrowsFileNotFoundException`
- `ReadFile_PaginatedSlice_ReturnsLinesAndCounts`
- `ReadFile_CrlfSlice_NormalizesToLf`
- `CreateDirectory_ExistingDirectory_ReturnsCreatedFalse`

**Delete**
- `DeleteFile_RemovesFileFromDisk`
- `DeleteFile_MissingFile_ThrowsFileNotFoundException`
- `DeleteDirectory_NonEmptyWithoutRecursive_ThrowsDirectoryNotEmptyException`
- `DeleteDirectory_Recursive_RemovesTree`
- `Delete_SandboxRoot_ThrowsArgumentException`

**Move**
- `MoveFile_RelocatesOnDisk`
- `MoveFile_ExistingDestinationNoOverwrite_ThrowsAlreadyExists`
- `MoveDirectory_RelocatesTreeOnDisk`
- `MoveDirectory_IntoItself_ThrowsArgumentException`

**Copy**
- `CopyFile_CopiesOnDiskLeavingSource`
- `CopyFile_ExistingDestinationNoOverwrite_ThrowsAlreadyExists`
- `CopyDirectory_CopiesTreeOnDisk`
- `CopyDirectory_IntoItself_ThrowsArgumentException`
- `Copy_SandboxRoot_ThrowsArgumentException`

**Edit, append, search, glob**
- `EditFile_UniqueMatch_ReplacesOnDisk`
- `EditFile_CrlfFileWithLfOldString_Replaces`
- `EditFile_MissingOldString_ThrowsOldStringNotFoundException`
- `EditFile_AmbiguousWithoutReplaceAll_ThrowsAmbiguousMatchException`
- `AppendFile_CreatesThenAppends`
- `SearchFiles_LiteralFindsHitsAndSkipsBinary`
- `SearchFiles_InvalidRegex_ThrowsArgumentException`
- `Glob_StarPattern_FindsTopLevelFiles`

### 7.3 What is not tested here

- Performance benchmarks (out of scope for a "simple" tools spec).
- Concurrent access (the service is registered Singleton; concurrent calls would serialise on filesystem locks, which is acceptable for an agent-driven tool that issues one call at a time).
- Cross-platform behaviour on Linux/macOS — only Windows is exercised by the build agent; the design is platform-aware but only Windows can be CI-validated.

---

## 8. Out of scope (deferred)

These are explicitly **not** in this spec and will be raised separately if needed:

- `file_delete`, `directory_delete`, `file_move`, `directory_move` (implemented as the `file_delete`, `directory_delete`, `file_move`, `directory_move` tools).
- `file_chmod`, `directory_chmod`.
- `file_copy`, `directory_copy` (implemented as the `file_copy` / `directory_copy` tools).
- Recursive directory deletion (implemented via `directory_delete` with `recursive=true`).
- File watching / tailing / streaming reads.
- Multi-root configuration (allowing `$HOME` or `%APPDATA%` in addition to `ProjectScope.Root`).
- Glob/recursive search returning content (only paths are returned today; implemented as the `file_search` tool).

---

## 9. Files added / changed

**Added:**

- `src/backend/Eling.Core/FileSystem/IFileSystemService.cs` — interface (15 methods).
- `src/backend/Eling.Core/FileSystem/*.cs` — result records and enums, one type per file: `PathKind`, `PathInfo`, `DirectoryCreateResult`, `DirectoryEntry`, `FileReadResult`, `FileWriteResult`, `GlobResult`, `DeleteResult`, `MoveResult`, `CopyResult`, `FileEditResult`, `FileAppendResult`, `ContentMatch`, `ContentSearchResult`.
- `src/backend/Eling.Core/Exceptions/*.cs` — filesystem exception types: `PathSandboxException`, `PathAlreadyExistsException`, `FileAlreadyExistsException`, `BinaryFileException`, `FileTooLargeException`, `NotADirectoryException`, `NotAFileException`, `DirectoryNotEmptyException`, `OldStringNotFoundException`, `AmbiguousMatchException`.
- `src/backend/Eling.Backend/FileSystem/FileSystemService.cs` — default `System.IO` implementation.
- `src/backend/Eling.Backend/Mcp/Tools/FileSystemTools.cs` — 15 `[McpServerTool]` methods.
- `tests/Eling.Backend.Tests/FileSystem/FakeFileSystemService.cs` — in-memory fake.
- `tests/Eling.Backend.Tests/FileSystem/FileSystemToolsTests.cs` — 59 wrapper tests.
- `tests/Eling.Backend.Tests/FileSystem/FileSystemServiceTests.cs` — 34 sandbox and IO tests.

**Changed:**

- `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs` — register `IFileSystemService` in both `AddElingCoreServices` overloads: `projectScope.Root` for the path-based overload, `chain.Head?.Root ?? chain.Cwd` for the scope-chain overload.
- `file_read` gained `offset`/`limit` pagination beyond the original spec; whole-file reads still return a plain string, and only paginated reads return the JSON envelope.

`WithToolsFromAssembly()` picks up `FileSystemTools` automatically.

**Tool inventory (15):** `path_test`, `directory_create`, `directory_list`, `glob`, `file_read`, `file_write`, `file_delete`, `directory_delete`, `file_move`, `directory_move`, `file_copy`, `directory_copy`, `file_edit`, `file_append`, `file_search`.

---

## 10. Open questions

None. The clarifications the design depended on were settled in the brainstorm:

- Sandbox: project root only (`ProjectScope.Root`).
- Tool set: originally six core tools (`path_test`, `directory_create`, `directory_list`, `glob`, `file_read`, `file_write`); the shipped surface is 15 tools (see §9).
- List output format: structured JSON.
- Tests: yes, with a `FakeFileSystemService` following the `FakeMemoryService` pattern.
- Tool naming: `file_*` / `directory_*` / `path_test` (resource-type grouping, not `filesystem_*`).
