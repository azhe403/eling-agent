# External Root Indexing via codebase_index — Design

- Date: 2026-10-06
- Status: Draft (pending user review)
- Scope: Eling backend + MCP `codebase_index` tool + Workspace catalog registration
- Deciders: eling backend (4417), MCP tool consumers

## 1. Context

Currently, `CodebaseIndexService` and `CodebaseIndexTool` only index files within the active workspace root (`_projectRoot`) where the Eling backend was started. The `paths` parameter on `codebase_index` only supports subpaths relative to the active workspace root.

Searching external codebases is already supported via `codebase_search` (with `projects: ["..."]` or `scope: "all"`), but building the index for an external folder currently requires launching a separate Eling backend process in that folder.

The user needs to be able to index an arbitrary external directory directly from the current session via `codebase_index` by specifying an optional root path.

## 2. Goals / Non-Goals

### Goals
- Allow `codebase_index` to index external directories outside the current workspace by passing an optional `root` parameter.
- Maintain dedicated SQLite databases for external roots (`~/.local/share/eling/codebase/<name>-<hash>.db`) via `ElingPaths.ResolveCodebaseDbPath`.
- Follow the `.gitignore` rules local to the external root.
- Record the external workspace in `IWorkspacesRegistry` (`projects.db`) so that `codebase_search(scope: "all")` and the dashboard pick it up seamlessly.
- Keep backwards compatibility: if `root` is omitted, index the current workspace as before.

### Non-Goals
- Background file watcher for external roots: **Explicitly disabled**. File watchers only run for the workspace where the Eling backend was launched. An external folder only gets a live watcher when opened as an active workspace.
- Memory operations on external roots: this feature only affects codebase FTS indexing and workspace catalog registration, not `.eling/memories/`.

## 3. Architecture & Contract

### 3.1 `codebase_index` MCP Tool Signature

Update `CodebaseIndexTool.cs`:
```csharp
[McpServerTool(Name = "codebase_index"), Description("Build or refresh the codebase FTS index (incremental by default, cache in the global codebase store).")]
public async Task<CodebaseIndexResponse> IndexAsync(
    [Description("true = rebuild all chunks, false = incremental by hash (default false).")] bool full = false,
    [Description("Optional scope, e.g. [\"src/backend\"]. Null = whole workspace.")] string[]? paths = null,
    [Description("Optional absolute directory path to index as an external workspace root. Null = current workspace.")] string? root = null,
    CancellationToken cancellationToken = default)
```

### 3.2 Root Validation & Resolution
When `root` is provided:
1. Validate format and exclusions via `ElingPaths.EnsureCodebaseRootsAllowed([root])`.
   - Must be fully qualified / absolute path.
   - Must not be inside temporal or excluded directories (`ELING_CODEBASE_EXCLUDE` / `.config/openchamber/chats`).
2. Verify directory existence on disk with `Directory.Exists(targetRoot)`. If not found, throw `DirectoryNotFoundException`.
3. Normalize path: `Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)`.

### 3.3 Indexing Execution Flow
- If `targetRoot` equals `_svc.ProjectRoot`:
  - Delegate to `_svc.IndexAsync(full, paths, cancellationToken)`.
- If `targetRoot` is external:
  1. Resolve DB path: `var dbPath = ElingPaths.ResolveCodebaseDbPath(targetRoot)`.
  2. Instantiate `SqliteCodebaseIndex(dbPath)`.
  3. Instantiate `CodebaseIndexService(targetRoot, index, _loggerFactory?.CreateLogger<CodebaseIndexService>())`.
  4. Run `IndexAsync(full, paths, cancellationToken)`.
  5. Record target root in `IWorkspacesRegistry` (`projects.db`):
     ```csharp
     var now = DateTimeOffset.UtcNow;
     _workspaces?.Record(new RegisteredWorkspace(
         string.Empty,
         targetRoot,
         targetRoot,
         CodebaseEnabled: true,
         FirstSeenAt: now,
         LastSeenAt: now));
     ```
  6. Trigger `_notifier.NotifyAsync("codebase", cancellationToken)` to notify UI tiles and dashboard.

## 4. Concurrency & Resource Management

- External `SqliteCodebaseIndex` instances are transient (`using var index = ...`) and disposed after indexing.
- SQLite connections use WAL mode and `busy_timeout = 5000` to prevent database locks if multiple calls touch the same database file.
- Single-flight inside `CodebaseIndexService` ensures no concurrent indexing runs on the same instance.
- No `FileSystemWatcher` is spawned for external roots.

## 5. Error Handling

- Invalid/relative root -> `ArgumentException` with clear diagnostic message.
- Missing root directory -> `DirectoryNotFoundException`.
- Excluded root -> `ArgumentException` (from `EnsureCodebaseRootsAllowed`).
- Permission/I/O error during file scanning -> logs warning, marks partial if directory enumeration fails, does not delete existing indexed files erroneously.

## 6. Testing Strategy

1. **Unit Tests (`CodebaseIndexToolTests.cs`)**:
   - `IndexAsync` with null `root` indexes local workspace.
   - `IndexAsync` with relative path throws `ArgumentException`.
   - `IndexAsync` with non-existent directory throws `DirectoryNotFoundException`.
   - `IndexAsync` with valid external directory indexes files into external DB, records into `IWorkspacesRegistry`, and does not activate a watcher.
2. **Integration Verification**:
   - Verify `codebase_search` with `projects: [externalRoot]` and `scope: "all"` returns hits from the newly indexed external root.
3. **Convention Tests**:
   - `CodeOrganizationConventionTests` and `CliWrapConventionTests` must remain green.
