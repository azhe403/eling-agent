# Inactive Scoped Projects Tracking & Last Active Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist discovered workspace projects and display their live/inactive/missing status with last active timestamp in Eling Dashboard, enabling offline memory browsing.

**Architecture:** A persistent `KnownProjectsStore` in user-level configuration (`~/.config/eling/projects.json`) tracks workspace roots, data directories, and last active timestamps. `RuntimeRegistry` synchronizes process lifecycles with the store, computes physical existence (`isExists`), and resolves memory services for offline projects. The Next.js frontend renders live, offline, and missing states with actionable indicators in the sidebar.

**Tech Stack:** C# .NET 10 (ASP.NET Core Minimal APIs, System.Text.Json), Next.js 16 (App Router, Tailwind CSS v4, Lucide icons, shadcn/ui), xUnit / MSTest for testing.

## Global Constraints

- Never use machine-specific absolute paths or real usernames in tests or code; use placeholders or temporary test roots.
- All JSON serialization must use `System.Text.Json` with source-generated contexts or standardized snake_case / camelCase as established.
- Offline memory access must never create missing directories without consent; only existing `.eling` directories on disk are resolved.
- Build and run tests per csproj individually using isolated artifacts (`.bin-test`).

---

### Task 1: Domain Models & KnownProjectsStore

**Files:**
- Create: `src/backend/Eling.Core/Runtime/ProjectRecord.cs`
- Create: `src/backend/Eling.Core/Runtime/IKnownProjectsStore.cs`
- Create: `src/backend/Eling.Core/Runtime/KnownProjectsStore.cs`
- Test: `tests/Eling.Core.Tests/KnownProjectsStoreTests.cs`

**Interfaces:**
- Produces:
  - `ProjectRecord(string ProjectRoot, string ProjectName, string DataDirectory, DateTimeOffset FirstSeen, DateTimeOffset LastActive)`
  - `IKnownProjectsStore`: `Task<IReadOnlyList<ProjectRecord>> GetAllAsync()`, `Task UpsertAsync(ProjectRecord record)`, `Task<bool> RemoveAsync(string projectRoot)`

- [ ] **Step 1: Write the failing unit tests for KnownProjectsStore**

```csharp
using Eling.Core.Runtime;
using Xunit;

namespace Eling.Core.Tests;

public class KnownProjectsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public KnownProjectsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-test-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task UpsertAndGetAll_PersistsProjects()
    {
        var store = new KnownProjectsStore(_tempDir);
        var record = new ProjectRecord
        {
            ProjectRoot = "C:\\work\\test-project",
            ProjectName = "test-project",
            DataDirectory = "C:\\work\\test-project\\.eling",
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastActive = DateTimeOffset.UtcNow
        };

        await store.UpsertAsync(record);
        var all = await store.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("C:\\work\\test-project", all[0].ProjectRoot);
        Assert.Equal("test-project", all[0].ProjectName);
    }

    [Fact]
    public async Task RemoveAsync_DeletesProject()
    {
        var store = new KnownProjectsStore(_tempDir);
        var record = new ProjectRecord
        {
            ProjectRoot = "C:\\work\\test-project",
            ProjectName = "test-project",
            DataDirectory = "C:\\work\\test-project\\.eling",
            FirstSeen = DateTimeOffset.UtcNow,
            LastActive = DateTimeOffset.UtcNow
        };

        await store.UpsertAsync(record);
        var removed = await store.RemoveAsync("C:\\work\\test-project");
        var all = await store.GetAllAsync();

        Assert.True(removed);
        Assert.Empty(all);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test`
Expected: FAIL compilation error (types not found).

- [ ] **Step 3: Implement ProjectRecord and KnownProjectsStore**

Create `ProjectRecord.cs`:
```csharp
namespace Eling.Core.Runtime;

public sealed class ProjectRecord
{
    public required string ProjectRoot { get; set; }
    public required string ProjectName { get; set; }
    public required string DataDirectory { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastActive { get; set; }
}
```

Create `IKnownProjectsStore.cs` and `KnownProjectsStore.cs` with thread-safe JSON file persistence at `projects.json`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test`
Expected: PASS

---

### Task 2: RuntimeRegistry Integration with KnownProjectsStore & Offline Memory Resolution

**Files:**
- Modify: `src/backend/Eling.Backend/RuntimeRegistry.cs`
- Create: `src/backend/Eling.Backend/Dtos/ScopedProjectDto.cs`
- Test: `tests/Eling.Backend.Tests/RuntimeRegistryProjectsTests.cs`

**Interfaces:**
- Consumes: `IKnownProjectsStore`, `ProjectRecord`
- Produces: `ScopedProjectDto`, `RuntimeRegistry.GetScopedProjects()`, offline resolution in `TryResolveMemoryServiceByProjectRoot()`

- [ ] **Step 1: Write failing unit test for RuntimeRegistry project tracking**

```csharp
using Eling.Backend;
using Eling.Core.Runtime;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

public class RuntimeRegistryProjectsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly UserScope _userScope;

    public RuntimeRegistryProjectsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eling-reg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _userScope = UserScope.Resolve(_tempRoot);
    }

    [Fact]
    public void UnregisteredProject_RemainsInScopedProjects_MarkedInactive()
    {
        var registry = new RuntimeRegistry(NullLogger<RuntimeRegistry>.Instance, _userScope);
        var projectDir = Path.Combine(_tempRoot, "my-project");
        var dataDir = Path.Combine(projectDir, ".eling");
        Directory.CreateDirectory(dataDir);

        registry.Register(new RuntimeRegistration
        {
            ProcessId = 99999,
            ProjectRoot = projectDir,
            DataDirectory = dataDir,
            StartTime = DateTimeOffset.UtcNow,
            McpEnabled = true,
            McpTransport = "stdio"
        });

        var projectsBefore = registry.GetScopedProjects();
        Assert.Contains(projectsBefore, p => p.ProjectRoot == projectDir && p.IsAlive && p.IsExists);

        registry.Unregister(99999);

        var projectsAfter = registry.GetScopedProjects();
        var project = Assert.Single(projectsAfter, p => p.ProjectRoot == projectDir);
        Assert.False(project.IsAlive);
        Assert.True(project.IsExists);

        // Offline memory resolution still works because directory exists
        var memService = registry.TryResolveMemoryServiceByProjectRoot(projectDir);
        Assert.NotNull(memService);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: FAIL (`GetScopedProjects` not defined).

- [ ] **Step 3: Update RuntimeRegistry and ScopedProjectDto**

Create `ScopedProjectDto.cs`:
```csharp
namespace Eling.Backend.Dtos;

public sealed class ScopedProjectDto
{
    public required string ProjectRoot { get; init; }
    public required string ProjectName { get; init; }
    public required string DataDirectory { get; init; }
    public required bool IsAlive { get; init; }
    public required bool IsExists { get; init; }
    public required DateTimeOffset LastActive { get; init; }
    public int? ProcessId { get; init; }
}
```

Update `RuntimeRegistry.cs`:
1. Initialize `IKnownProjectsStore` pointing to `_userScope.ConfigDirectory`.
2. In `Register()`: upsert project record.
3. In `Heartbeat()`: update `LastActive`.
4. In `GetScopedProjects()`: return all known projects joined with live runtimes, computing `IsExists = Directory.Exists(p.ProjectRoot)`.
5. In `TryResolveMemoryServiceByProjectRoot()`: if runtime is not alive, check if `Directory.Exists(dataDirectory)`; if so, instantiate and cache `MemoryService`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: PASS

---

### Task 3: Backend HTTP Endpoints

**Files:**
- Modify: `src/backend/Eling.Backend/CoordinatorEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Endpoints/ScopedMemoryEndpoints.cs`
- Test: `tests/Eling.Backend.Tests/ProjectEndpointsTests.cs`

**Interfaces:**
- Produces: `GET /api/coordinator/runtimes`, `GET /api/projects`, `DELETE /api/projects?projectRoot={path}`

- [ ] **Step 1: Write integration tests for projects endpoints**

```csharp
// Test GET /api/coordinator/runtimes returns ScopedProjectDto with isAlive and isExists
// Test DELETE /api/projects removes the project from persistent store
```

- [ ] **Step 2: Implement endpoint routing in CoordinatorEndpoints.cs**

Update `/api/coordinator/runtimes` or add `/api/projects` endpoints to expose the list and deletion logic.

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: PASS

---

### Task 4: Frontend UI Updates (Sidebar & Project Indicators)

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/lib/types.ts`
- Modify: `src/frontend/Eling.Dashboard/src/components/app-sidebar.tsx`
- Modify: `src/frontend/Eling.Dashboard/src/lib/date-utils.ts`

**Interfaces:**
- Consumes: `ScopedProjectDto` (`projectRoot`, `projectName`, `isAlive`, `isExists`, `lastActive`)
- Produces: Visual status indicators (Live 🟢, Inactive ⚪ with relative time, Missing ⚠️ with remove button).

- [ ] **Step 1: Update TypeScript types in `src/lib/types.ts`**

```typescript
export interface Runtime {
  processId?: number
  projectRoot: string
  projectName?: string
  dataDirectory: string
  isAlive: boolean
  isExists: boolean
  lastActive: string
  startTime?: string
  mcpEnabled?: boolean
  mcpTransport?: string
}
```

- [ ] **Step 2: Update `app-sidebar.tsx`**

Render visual badges:
- If `isAlive`: green live indicator dot.
- If `!isAlive && isExists`: muted text with formatted `lastActive` (e.g. `formatRelativeTime(r.lastActive)`).
- If `!isExists`: warning badge "Folder not found" with a delete/forget icon button.

- [ ] **Step 3: Build frontend and verify typecheck**

Run: `pnpm --prefix src/frontend/Eling.Dashboard build`
Expected: Build succeeds with zero TypeScript errors.

---

### Task 5: End-to-End Solution Build & Test Validation

**Files:**
- Test all: Solution test run

- [ ] **Step 1: Run complete Core tests**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test`
Expected: 100% PASS

- [ ] **Step 2: Run complete Backend tests**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: 100% PASS

- [ ] **Step 3: Build backend with dashboard packaging**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: Clean build and dashboard export.
