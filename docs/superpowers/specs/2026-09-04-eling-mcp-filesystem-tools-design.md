# Filesystem MCP Tools — Design

**Date:** 2026-09-04
**Status:** Draft (pending user review of written spec)
**Scope:** Eling backend (`Eling.Backend`, `Eling.Core`) + new tests in `Eling.Backend.Tests`

---

## 1. Goal

Add six simple MCP tools so an agent can test, create, list, glob, read, and write filesystem paths under the project root:

1. `path_test` — check whether a path exists and report its kind (file / directory / symlink) and size.
2. `directory_create` — make a directory (and any missing parents), idempotent.
3. `directory_list` — list the contents of a directory, optionally recursive with a depth cap and a glob pattern.
4. `glob` — search for files/directories matching a glob pattern under a given path, with a result cap.
5. `file_read` — read a small text file (with a byte cap and a binary detector).
6. `file_write` — write a text file, creating parent directories on demand.

These tools mirror the shape of the existing `memory_*` tools: a thin MCP wrapper that calls an `IFileSystemService` from `Eling.Core`, with all path resolution and sandboxing implemented inside the service so it can be unit-tested with a fake. They are not a general-purpose filesystem API — they are scoped to the project root, they reject binary files, and they cap sizes so an agent cannot accidentally fill a context window or a disk.

The drivers are:

1. **Symmetry with `memory_*`.** The agent already has a `memory_save`/`memory_get` workflow; basic file operations are the obvious next step.
2. **Testability without real disk.** All file logic is behind `IFileSystemService`, so unit tests run against an in-memory fake, not a temp directory on the build host.
3. **Bounded blast radius.** Every operation is sandboxed under `ProjectScope.Root`; binary detection and size caps prevent accidental context-window or disk exhaustion.

The non-goals are:

- General filesystem scripting (`delete`, `move`, `chmod`, `chown`, recursive delete). These are destructive and can wait for a follow-up spec.
- Multi-root access (e.g. `$HOME`, `%APPDATA%`). Out of scope; the only allowed root is `ProjectScope.Root` at this time.
- File watching, tailing, or streaming reads. Read returns the whole file or fails.
- Symlink creation or symlink-target resolution that crosses the sandbox boundary. The service resolves the input path; it does not follow symlinks whose targets land outside the root.

---

## 2. Design summary

A new `IFileSystemService` lives in `Eling.Core` with six methods matching the six tools. The default implementation `FileSystemService` lives in `Eling.Backend` and uses `System.IO` (`File`, `Directory`, `Path`) plus a `rootPath` injected from `ProjectScope.Root`. A new `FileSystemTools` class in `Eling.Backend/Mcp/Tools/` is the thin MCP wrapper and is auto-discovered by `WithToolsFromAssembly()` (no changes to `McpServiceExtensions`).

`FileSystemService` accepts the root path through its constructor (not via `ProjectScope` directly) so the unit test can pass a sandbox directory. In production, `AddElingMcpServerStdio` wires the root from `ProjectScope.Root`. The service does its own `Path.GetFullPath` resolution and rejects any input whose resolved path is not under the root — this is the only sandbox enforcement, and it is unit-tested.

The five MCP tools return JSON-serialised results (one object per call) for the JSON-shaped operations and a plain string for `file_read` (because the content is itself text and the agent does not need to parse a wrapper). Errors that the agent can act on (sandbox violation, not found, binary detected, would overwrite without permission) come back as `{ "ok": false, "error": "..." }` so the agent can branch on the shape; unexpected exceptions propagate and are surfaced by the MCP layer.

A `FakeFileSystemService` is added to `tests/Eling.Backend.Tests/` mirroring `FakeMemoryService` so `FileSystemTools` can be unit-tested without touching disk. The fake is an in-memory dictionary of path → entry with file content and directory children; the sandbox root is just another string passed to its constructor.

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
    IReadOnlyList<DirectoryEntry> ListDirectory(string path, bool recursive, int maxDepth, string? pattern);
    IReadOnlyList<DirectoryEntry> Glob(string basePath, string pattern, int maxDepth, int maxResults);
    FileReadResult ReadFile(string path, int maxBytes);
    FileWriteResult WriteFile(string path, string content, bool overwrite);
}
```

Result types are records defined alongside the interface in `Eling.Core.FileSystem`. They use PascalCase property names; the MCP wrapper converts to camelCase JSON at the edge.

```csharp
public sealed record PathInfo(
    string ResolvedPath,
    bool Exists,
    PathKind Kind,
    long? SizeBytes);

public enum PathKind { NotFound, File, Directory, Symlink, Other }

public sealed record DirectoryCreateResult(
    string ResolvedPath,
    bool Created); // false when the directory already existed

public sealed record DirectoryEntry(
    string Path,
    string Name,
    PathKind Kind,
    long? SizeBytes);

public sealed record FileReadResult(
    string ResolvedPath,
    long SizeBytes,
    string Content);

public sealed record FileWriteResult(
    string ResolvedPath,
    long SizeBytes,
    bool Overwrote); // true when the file existed and was replaced

public sealed record GlobResult(
    string ResolvedBasePath,
    string Pattern,
    int MatchCount,
    bool Truncated,
    IReadOnlyList<DirectoryEntry> Matches);
```

The service throws `PathSandboxException` (defined in the same namespace) when the resolved path is outside the configured root. The MCP wrapper catches it and turns it into a JSON error. Other predictable failures (`FileNotFoundException`, `DirectoryNotFoundException`, `FileAlreadyExistsException`, `BinaryFileException`, `FileTooLargeException`) follow the same pattern: defined in Core, caught at the tool boundary.

### 3.2 `FileSystemService` (Backend)

Location: `src/backend/Eling.Backend/FileSystem/FileSystemService.cs`

```csharp
namespace Eling.Backend.FileSystem;

public sealed class FileSystemService : IFileSystemService
{
    private readonly string _rootPath;
    private readonly int _defaultReadMaxBytes;
    private readonly int _defaultMaxDepth;

    public FileSystemService(string rootPath, int defaultReadMaxBytes = 1_048_576, int defaultMaxDepth = 3)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _defaultReadMaxBytes = defaultReadMaxBytes;
        _defaultMaxDepth = defaultMaxDepth;
    }

    // ... methods per section 4
}
```

The service is registered in `McpServiceExtensions.AddElingMcpServerStdio` with the root from `ProjectScope.Root`:

```csharp
services.AddSingleton<IFileSystemService>(sp =>
{
    var scope = sp.GetRequiredService<ProjectScope>();
    return new FileSystemService(scope.Root);
});
```

### 3.3 `FileSystemTools` (Backend MCP wrapper)

Location: `src/backend/Eling.Backend/Mcp/Tools/FileSystemTools.cs`

The class is annotated `[McpServerToolType]` and each method is `[McpServerTool(Name = "...", Description = "...")]`. The wrapper is short on purpose: it resolves parameters, calls the service, and serialises the result. Sandbox exceptions become JSON error objects; the agent gets a structured failure it can branch on.

```csharp
[McpServerToolType]
public sealed class FileSystemTools
{
    private readonly IFileSystemService _fs;
    private readonly ILogger<FileSystemTools> _log;

    public FileSystemTools(IFileSystemService fs, ILogger<FileSystemTools> log)
    {
        _fs = fs;
        _log = log;
    }

    [McpServerTool(Name = "path_test"), Description("Check whether a path exists under the project root and report its kind (file / directory / symlink) and size. Read-only.")]
    public string PathTest(
        [Description("Absolute or project-relative path.")] string path)
    { /* ... */ }
    // ... four more methods, all the same shape
}
```

`WithToolsFromAssembly()` in `McpServiceExtensions` picks the class up automatically — no manual registration needed.

### 3.4 `FakeFileSystemService` (Tests)

Location: `tests/Eling.Backend.Tests/FileSystem/FakeFileSystemService.cs`

Mirrors `FakeMemoryService` from the memory tests. Backed by a `Dictionary<string, FakeEntry>` keyed by normalised full path. `FakeEntry` has a `Kind` and, for files, a `Content` string and a `SizeBytes` long. Sandbox enforcement is identical to the real service (same `IsWithinRoot` helper, extracted into a static internal class so both implementations share it).

---

## 4. Sandbox and path resolution

### 4.1 Resolution rule

Every method on `IFileSystemService` runs the input through this sequence:

1. Reject null / empty / whitespace input with `ArgumentException`.
2. Compute `full = Path.GetFullPath(Path.Combine(_rootPath, input))` so relative inputs land under the root and absolute inputs are still normalised.
3. Compute `rootWithSep = AppendTrailingSeparator(_rootPath)`.
4. If `!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)` on Windows or `StringComparison.Ordinal` on Linux/macOS, throw `PathSandboxException`.
5. Return `full` as `ResolvedPath` on the result record.

`AppendTrailingSeparator` is the standard pattern for preventing the `/foo-bar` vs `/foo` confusion: if the root is `C:\some-folder\Eling` then the comparison string is `C:\some-folder\Eling\`, and `C:\some-folder\ElingOther` is correctly rejected.

The platform comparison is selected at construction time (one branch, not per call):

```csharp
_rootComparison = OperatingSystem.IsWindows()
    ? StringComparison.OrdinalIgnoreCase
    : StringComparison.Ordinal;
```

### 4.2 Symlinks

The service does **not** follow symlinks during sandbox resolution. The rule is: if the input path is a symlink, the resolved path of the link itself is what we check against the sandbox. We do not chase it to a target outside the root. This is the safe default and matches the principle that an agent should be able to reason about what it is touching without surprise indirection.

For `path_test`, the service reports `Kind = Symlink` when `File.ResolveLinkTarget` returns non-null and the target itself is not asked about. `directory_list` does not follow symlinks into other directories — symlinks to directories are listed with `Kind = Symlink`, not expanded.

### 4.3 Defaults and limits

| Setting | Default | Where set | Notes |
|---|---|---|---|
| Read byte cap | 1 MiB (1_048_576) | `FileSystemService` ctor | Reject larger files; do not stream. |
| Recursive depth cap (list) | 3 | `FileSystemService` ctor | Hard upper bound of 5 enforced by the MCP wrapper regardless. |
| Recursive depth cap (glob) | 5 | per call | Hard upper bound of 10 enforced by the MCP wrapper. |
| Glob result cap | 200 | per call | Hard upper bound of 1000 enforced by the MCP wrapper. |
| Glob pattern | none (all entries) | per call | Standard `*` / `?` wildcards via `Directory.EnumerateFiles` matchType. `**` for cross-directory recursion. |
| Overwrite on `file_write` | `false` | per call | When `false` and file exists, return error; do not write. |

The MCP wrapper clamps `maxDepth` to `[0, 5]` (list) / `[0, 10]` (glob) and `maxResults` to `[1, 1000]` before calling the service, so a misbehaving caller cannot ask for unlimited recursion or a million results. The service itself accepts any non-negative integer; the clamp is at the trust boundary.

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

When `exists` is `false`, `kind` is `NotFound` and `sizeBytes` is `null`. Never throws for not-found; only throws for sandbox violation.

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
| `pattern` | string | no | `null` | Glob like `*.cs` applied to file names. |

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
| `maxBytes` | int | no | `1_048_576` | Hard upper bound (1 MiB). The wrapper also clamps user input to this. |

Returns a plain string (the file content), not a JSON object. Reasoning: the content is the text the agent wants, and wrapping it in JSON would force the agent to unwrap just to read the file. Errors still come back as JSON (`{"ok": false, "error": "..."}`) so the shape difference is the signal.

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

---

## 6. Error handling

The MCP wrapper returns `{ "ok": false, "error": "...", "code": "..." }` for every predictable failure so the agent can branch on `code`. `code` is one of:

| Code | Meaning | When |
|---|---|---|
| `sandbox_violation` | Path is outside the project root. | `PathSandboxException` |
| `not_found` | The path does not exist. | `FileNotFoundException` / `DirectoryNotFoundException` |
| `already_exists` | `file_write` with `overwrite=false` and file present. | `FileAlreadyExistsException` |
| `binary_file` | `file_read` on a file with NUL bytes in the first 8 KiB. | `BinaryFileException` |
| `file_too_large` | `file_read` with size > `maxBytes`. | `FileTooLargeException` |
| `not_a_directory` | `directory_list` on a file path. | `NotADirectoryException` |
| `not_a_file` | `file_read`/`file_write` on a directory path. | `NotAFileException` |
| `invalid_argument` | Bad parameter (null, empty, negative depth). | `ArgumentException` |
| `internal_error` | Anything else. | Any other exception |

For `path_test`, only `sandbox_violation`, `invalid_argument`, and `internal_error` apply — the call is read-only, so `not_found` is reported as `{ "exists": false }` rather than as an error.

Success responses for `file_read` are a plain string; for everything else they are the JSON shapes shown in section 5.

---

## 7. Tests

### 7.1 Unit tests — `FileSystemToolsTests`

Location: `tests/Eling.Backend.Tests/FileSystem/FileSystemToolsTests.cs`

Uses `FakeFileSystemService` to drive each tool. Cases:

- `PathTest_ExistingFile_ReturnsFileInfo`
- `PathTest_ExistingDirectory_ReturnsDirectoryInfo`
- `PathTest_NonExistent_ReturnsNotFound`
- `PathTest_OutsideRoot_ReturnsSandboxError`
- `DirectoryCreate_NewDirectory_ReturnsCreatedTrue`
- `DirectoryCreate_ExistingDirectory_ReturnsCreatedFalse`
- `DirectoryCreate_OutsideRoot_ReturnsSandboxError`
- `DirectoryList_Flat_ReturnsEntries`
- `DirectoryList_Recursive_ReturnsNestedEntries`
- `DirectoryList_PatternFilter_ReturnsMatchesOnly`
- `DirectoryList_OutsideRoot_ReturnsSandboxError`
- `Glob_StarPattern_ReturnsDirectChildrenOnly`
- `Glob_DoubleStarPattern_ReturnsAllDescendants`
- `Glob_DirectoryMatch_ReturnsDirectoryEntry`
- `Glob_TruncationFlag_SetWhenMaxResultsExceeded`
- `Glob_OutsideRoot_ReturnsSandboxError`
- `FileRead_ExistingTextFile_ReturnsContent`
- `FileRead_BinaryFile_ReturnsBinaryError`
- `FileRead_OversizedFile_ReturnsFileTooLargeError`
- `FileRead_OutsideRoot_ReturnsSandboxError`
- `FileWrite_NewFile_CreatesParentAndFile`
- `FileWrite_ExistingFileNoOverwrite_ReturnsAlreadyExistsError`
- `FileWrite_ExistingFileOverwrite_ReplacesContent`

### 7.2 Sandbox tests — `FileSystemServiceTests`

Location: `tests/Eling.Backend.Tests/FileSystem/FileSystemServiceTests.cs`

Uses a real `FileSystemService` against `Path.GetTempPath()` / a per-test subdir, then cleans up. Cases:

- `ResolvePath_RelativeInput_LandsUnderRoot`
- `ResolvePath_AbsoluteInputUnderRoot_Accepted`
- `ResolvePath_AbsoluteInputOutsideRoot_ThrowsPathSandboxException`
- `ResolvePath_ParentTraversal_Throws`  (e.g. `root/foo/../../etc/passwd`)
- `ReadFile_BinaryContent_ThrowsBinaryFileException`
- `ReadFile_OversizedContent_ThrowsFileTooLargeException`

### 7.3 What is not tested here

- Performance benchmarks (out of scope for a "simple" tools spec).
- Concurrent access (the service is registered Singleton; concurrent calls would serialise on filesystem locks, which is acceptable for an agent-driven tool that issues one call at a time).
- Cross-platform behaviour on Linux/macOS — only Windows is exercised by the build agent; the design is platform-aware but only Windows can be CI-validated.

---

## 8. Out of scope (deferred)

These are explicitly **not** in this spec and will be raised separately if needed:

- `file_delete`, `directory_delete`, `file_move`, `directory_move`.
- `file_chmod`, `directory_chmod`.
- `file_copy`, `directory_copy`.
- Recursive directory deletion.
- File watching / tailing / streaming reads.
- Multi-root configuration (allowing `$HOME` or `%APPDATA%` in addition to `ProjectScope.Root`).
- Glob/recursive search returning content (only paths are returned today; a future `content_search` tool is a separate spec).

---

## 9. Files added / changed

**Added:**

- `src/backend/Eling.Core/FileSystem/IFileSystemService.cs` — interface + result records + exception types.
- `src/backend/Eling.Backend/FileSystem/FileSystemService.cs` — default implementation.
- `src/backend/Eling.Backend/Mcp/Tools/FileSystemTools.cs` — six `[McpServerTool]` methods.
- `tests/Eling.Backend.Tests/FileSystem/FakeFileSystemService.cs` — in-memory fake.
- `tests/Eling.Backend.Tests/FileSystem/FileSystemToolsTests.cs` — wrapper unit tests.
- `tests/Eling.Backend.Tests/FileSystem/FileSystemServiceTests.cs` — sandbox unit tests.

**Changed:**

- `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs` — register `IFileSystemService` with `ProjectScope.Root` as the sandbox root. One line in the existing DI block.

No other files are modified. `WithToolsFromAssembly()` picks up `FileSystemTools` automatically.

---

## 10. Open questions

None. The clarifications the design depended on were settled in the brainstorm:

- Sandbox: project root only (`ProjectScope.Root`).
- Tool set: all six (`path_test`, `directory_create`, `directory_list`, `glob`, `file_read`, `file_write`).
- List output format: structured JSON.
- Tests: yes, with a `FakeFileSystemService` following the `FakeMemoryService` pattern.
- Tool naming: `file_*` / `directory_*` / `path_test` (resource-type grouping, not `filesystem_*`).
