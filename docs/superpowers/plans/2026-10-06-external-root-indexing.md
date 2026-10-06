# External Root Indexing via codebase_index Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable `codebase_index` MCP tool to index external directories outside the workspace into dedicated SQLite DBs and register them in `projects.db` for global search.

**Architecture:** Extend `CodebaseIndexTool.IndexAsync` with an optional `root` parameter. Validate external roots with `ElingPaths.EnsureCodebaseRootsAllowed`, instantiate an isolated `CodebaseIndexService` over `ElingPaths.ResolveCodebaseDbPath(targetRoot)`, execute indexing, and record the root in `IWorkspacesRegistry`.

**Tech Stack:** .NET 10, C# 14, Microsoft.Data.Sqlite, ModelContextProtocol SDK, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-06-external-root-indexing-design.md`

## Global Constraints

- One top-level type per file; records with >1 parameter must be chopped one-param-per-line.
- Tool DTOs stay in `Dtos/`.
- No `ValueTuple` usage.
- External commands via CliWrap only (not applicable to this feature).
- External roots must never spawn a file watcher; watchers only run for the active workspace.
- Reject relative paths and excluded/temporal directories with `ArgumentException`.

## Review Focus

- Relative path passed as `root`: must throw `ArgumentException` with clear message naming the path.
- Non-existent directory passed as `root`: must throw `DirectoryNotFoundException`.
- Excluded directory passed as `root`: must be rejected by `EnsureCodebaseRootsAllowed`.
- Re-indexing existing external root: must update existing DB incrementally without duplicate catalog rows.
- Current workspace passed explicitly as `root`: must route to `_svc` without redundant instantiation or extra registry overhead.

---

### Task 1: DI Registration of `IWorkspacesRegistry` in Core Services

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs:80-140`
- Test: `tests/Eling.Backend.Tests/ScopeChainDiTests.cs`

**Interfaces:**
- Produces: `IWorkspacesRegistry` available via dependency injection for MCP tool constructors.

- [ ] **Step 1: Write test for DI resolution of `IWorkspacesRegistry`**

In `tests/Eling.Backend.Tests/ScopeChainDiTests.cs`:
Verify `provider.GetService<IWorkspacesRegistry>()` returns non-null instance backed by `userScope.ProjectsDatabasePath`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter FullyQualifiedName~ScopeChainDiTests`
Expected: FAIL (service not registered).

- [ ] **Step 3: Register `IWorkspacesRegistry` in `McpServiceExtensions.AddElingCoreServices`**

In `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`:
```csharp
services.TryAddSingleton<IWorkspacesRegistry>(sp => new SqliteWorkspacesRegistry(
    userScope.ProjectsDatabasePath,
    sp.GetService<ILogger<SqliteWorkspacesRegistry>>()));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter FullyQualifiedName~ScopeChainDiTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs tests/Eling.Backend.Tests/ScopeChainDiTests.cs
git commit -m "feat(di): register IWorkspacesRegistry in core MCP services"
```

---

### Task 2: Implement External Root Indexing in `CodebaseIndexTool`

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/Tools/CodebaseIndexTool.cs`
- Create: `tests/Eling.Backend.Tests/CodebaseIndexToolTests.cs`

**Interfaces:**
- Consumes: `ElingPaths.EnsureCodebaseRootsAllowed`, `ElingPaths.ResolveCodebaseDbPath`, `IWorkspacesRegistry.Record`, `CodebaseIndexService`.
- Produces: `CodebaseIndexTool.IndexAsync(bool full = false, string[]? paths = null, string? root = null, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write failing tests in `CodebaseIndexToolTests.cs`**

Tests covering:
1. `IndexAsync_WithoutRoot_IndexesCurrentWorkspace`
2. `IndexAsync_WithRelativeRoot_ThrowsArgumentException`
3. `IndexAsync_WithNonExistentRoot_ThrowsDirectoryNotFoundException`
4. `IndexAsync_WithExternalRoot_IndexesIntoDedicatedDb_AndRecordsInRegistry`
5. `IndexAsync_WithSameAsCurrentWorkspaceRoot_RoutesToDefaultService`

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter FullyQualifiedName~CodebaseIndexToolTests`
Expected: FAIL (parameter `root` does not exist or method not updated).

- [ ] **Step 3: Update `CodebaseIndexTool` constructor and `IndexAsync` signature & implementation**

1. Update constructor:
```csharp
public CodebaseIndexTool(
    CodebaseIndexService svc,
    ILogger<CodebaseIndexTool>? logger = null,
    IMemoryChangeNotifier? notifier = null,
    IWorkspacesRegistry? workspaces = null,
    ILoggerFactory? loggerFactory = null)
```
2. Update `IndexAsync`:
```csharp
[McpServerTool(Name = "codebase_index"), Description("Build or refresh the codebase FTS index (incremental by default, cache in the global codebase store).")]
public async Task<CodebaseIndexResponse> IndexAsync(
    [Description("true = rebuild all chunks, false = incremental by hash (default false).")] bool full = false,
    [Description("Optional scope, e.g. [\"src/backend\"]. Null = whole workspace.")] string[]? paths = null,
    [Description("Optional absolute directory path to index as an external workspace root. Null = current workspace.")] string? root = null,
    CancellationToken cancellationToken = default)
```
3. Implement root validation:
   - If `!string.IsNullOrWhiteSpace(root)`:
     - Call `ElingPaths.EnsureCodebaseRootsAllowed([root])`.
     - Normalize: `var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);`
     - If `!Directory.Exists(normalized)`: throw `DirectoryNotFoundException($"Directory not found: '{normalized}'.")`.
     - If not equal to `_svc.ProjectRoot`: execute index pass with new `CodebaseIndexService` over `ElingPaths.ResolveCodebaseDbPath(normalized)`, record in `_workspaces?.Record(...)`, and notify.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter FullyQualifiedName~CodebaseIndexToolTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Backend/Mcp/Tools/CodebaseIndexTool.cs tests/Eling.Backend.Tests/CodebaseIndexToolTests.cs
git commit -m "feat(codebase): support external root indexing in codebase_index"
```

---

### Task 3: Integration Verification & Convention Guard

**Files:**
- Test: `tests/Eling.Backend.Tests/CodebaseIndexToolTests.cs`

- [ ] **Step 1: Write integration test verifying search across external indexed root**

In `CodebaseIndexToolTests.cs`:
1. Create temporary external directory with sample code files.
2. Index via `codebase_index(root: externalDir)`.
3. Perform `codebase_search(query: "...", scope: "all")` or `projects: [externalDir]`.
4. Assert hit is returned with `ProjectRoot == externalDir`.

- [ ] **Step 2: Run all backend tests and convention tests**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj`
Expected: All tests PASS, including `CodeOrganizationConventionTests` and `CliWrapConventionTests`.

- [ ] **Step 3: Commit**

```bash
git add tests/Eling.Backend.Tests/CodebaseIndexToolTests.cs
git commit -m "test(codebase): add integration test for external root search"
```
