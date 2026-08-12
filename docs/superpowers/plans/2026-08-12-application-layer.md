# Application / Use-Case Layer Implementation Plan (Pecut 5)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create `Eling.Application` — the orchestration/use-case layer that composes `Eling.Storage` and `Eling.Index` into the memory operations consumed by MCP and the REST server. Business logic lives here, never in `Eling.Mcp` or `Eling.Server`.

**Architecture:** A new `Eling.Application` class library exposing `IMemoryService` (the single use-case facade). `MemoryService` holds `IMemoryStorage` + `IMemoryIndex` and composes them; it knows nothing about files, SQLite, or HTTP. `Eling.Mcp` and `Eling.Server` later depend on this layer, not on Storage/Index directly.

**Tech Stack:** .NET 10, C# 14, xUnit.

## Global Constraints

- Work ONLY in `src/backend/Eling.Application/`, `tests/Eling.Application.Tests/`, and the solution file `Eling.slnx`.
- `Eling.Application` references `Eling.Core`, `Eling.Storage`, and `Eling.Index` — and nothing else.
- Do NOT modify `Eling.Core`, `Eling.Storage`, `Eling.Index`, `Eling.Graph`, `Eling.Mcp`, `Eling.Server`, or `src/frontend/`.
- Do NOT introduce persistence, HTTP, MCP, or DI-container-specific logic in `Eling.Application` (no `WebApplicationBuilder`, no `AddSingleton`, no SQL).
- No new NuGet packages: this layer is pure composition of existing interfaces.
- `Eling.Graph` stays a placeholder — NOT part of this pecut.

---

### Task 1: Create the `Eling.Application` project

**Files:**
- Create: `src/backend/Eling.Application/Eling.Application.csproj`
- Modify: `Eling.slnx` (add project to `/src/backend/` folder)

- [ ] **Step 1: Create `Eling.Application.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Eling.Core\Eling.Core.csproj" />
    <ProjectReference Include="..\Eling.Storage\Eling.Storage.csproj" />
    <ProjectReference Include="..\Eling.Index\Eling.Index.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add project to `Eling.slnx`**

In `Eling.slnx`, inside the `<Folder Name="/src/backend/">` element, add alphabetically (after `Eling.Core`, before `Eling.Graph`):

```xml
<Project Path="src/backend/Eling.Application/Eling.Application.csproj" />
```

- [ ] **Step 3: Build the solution**
Run: `dotnet build Eling.slnx`

- [ ] **Step 4: Commit**
Commit message: `chore: add Eling.Application project (Pecut 5)`

---

### Task 2: Define `IMemoryService` — the use-case facade

**Files:**
- Create: `src/backend/Eling.Application/IMemoryService.cs`

- [ ] **Step 1: Create `IMemoryService.cs`**

```csharp
using Eling.Core;
using Eling.Index;

namespace Eling.Application;

public interface IMemoryService
{
    Task<Memory> SaveAsync(Memory memory);
    Task<Memory?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Memory>> ListAllAsync();
    Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query);
    Task RebuildIndexAsync();
}
```

Rationale (documented in the plan, not as code comments):
- `SaveAsync` returns the saved `Memory` (id/timestamps may be assigned by the domain).
- `SearchAsync` returns `MemorySearchResult` (id + rank) from `Eling.Index` — results are already tied to persisted memories, so returning full `Memory` objects is unnecessary for the index contract.
- `RebuildIndexAsync` re-derives the entire index from canonical storage (single source of truth).

- [ ] **Step 2: Build `Eling.Application`**
Run: `dotnet build src/backend/Eling.Application/Eling.Application.csproj`

---

### Task 3: Implement `MemoryService`

**Files:**
- Create: `src/backend/Eling.Application/MemoryService.cs`

- [ ] **Step 1: Create `MemoryService.cs`**

```csharp
using Eling.Core;
using Eling.Index;
using Eling.Storage;

namespace Eling.Application;

public class MemoryService : IMemoryService
{
    private readonly IMemoryStorage _storage;
    private readonly IMemoryIndex _index;

    public MemoryService(IMemoryStorage storage, IMemoryIndex index)
    {
        _storage = storage;
        _index = index;
    }

    public async Task<Memory> SaveAsync(Memory memory)
    {
        await _storage.SaveAsync(memory);
        await _index.IndexAsync(memory);
        return memory;
    }

    public Task<Memory?> GetByIdAsync(MemoryId id) => _storage.GetByIdAsync(id);

    public async Task<bool> DeleteAsync(MemoryId id)
    {
        if (!await _storage.DeleteAsync(id))
        {
            return false;
        }
        await _index.RemoveAsync(id);
        return true;
    }

    public Task<IReadOnlyCollection<Memory>> ListAllAsync() => _storage.ListAllAsync();

    public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query) => _index.SearchAsync(query);

    public async Task RebuildIndexAsync()
    {
        var memories = await _storage.ListAllAsync();
        await _index.RebuildAsync(memories);
    }
}
```

- [ ] **Step 2: Delete leftover placeholder**
Confirm there is no `Class1.cs` in `src/backend/Eling.Application/` (a fresh project has none; if created by the template, delete it).

- [ ] **Step 3: Build `Eling.Application`**
Run: `dotnet build src/backend/Eling.Application/Eling.Application.csproj`

---

### Task 4: Create the `Eling.Application.Tests` project

**Files:**
- Create: `tests/Eling.Application.Tests/Eling.Application.Tests.csproj`
- Modify: `Eling.slnx` (add project to `/tests/` folder)

- [ ] **Step 1: Create `Eling.Application.Tests.csproj`** (mirror the xUnit pattern of existing test projects)

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\backend\Eling.Application\Eling.Application.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add project to `Eling.slnx`**
In `Eling.slnx`, inside `<Folder Name="/tests/">`, add (alphabetically, after `Eling.Core.Tests`):

```xml
<Project Path="tests/Eling.Application.Tests/Eling.Application.Tests.csproj" />
```

- [ ] **Step 3: Restore + build**
Run: `dotnet build Eling.slnx`

---

### Task 5: Test `MemoryService` composition

**Files:**
- Create: `tests/Eling.Application.Tests/MemoryServiceTests.cs`

Use in-memory fakes for `IMemoryStorage` and `IMemoryIndex` (a `Dictionary<MemoryId, Memory>` for storage, an `IndexAsync`/`RemoveAsync`/`RebuildAsync` recording fake for the index). No mocking library — hand-rolled fakes match existing test conventions.

- [ ] **Step 1: Write `MemoryServiceTests.cs`**

Cover at minimum:

- `SaveAsync` persists to storage AND indexes the memory.
- `GetByIdAsync` returns a saved memory; returns `null` for unknown id.
- `DeleteAsync` removes from storage and index when present; returns `false` and does NOT touch the index when absent.
- `ListAllAsync` returns all saved memories (order-insensitive assertion).
- `SearchAsync` delegates the raw query to the index and returns its results unchanged.
- `RebuildIndexAsync` reads all memories from storage and passes them to the index `RebuildAsync`.

```csharp
using Eling.Application;
using Eling.Core;
using Eling.Index;

namespace Eling.Application.Tests;

public class MemoryServiceTests
{
    private sealed class FakeStorage : IMemoryStorage
    {
        public readonly Dictionary<MemoryId, Memory> Items = new();
        public Task SaveAsync(Memory memory) { Items[memory.Id] = memory; return Task.CompletedTask; }
        public Task<Memory?> GetByIdAsync(MemoryId id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<bool> DeleteAsync(MemoryId id) => Task.FromResult(Items.Remove(id));
        public Task<IReadOnlyCollection<Memory>> ListAllAsync() =>
            Task.FromResult<IReadOnlyCollection<Memory>>(Items.Values.ToList());
    }

    private sealed class FakeIndex : IMemoryIndex
    {
        public readonly List<Memory> Indexed = new();
        public readonly List<MemoryId> Removed = new();
        public IEnumerable<Memory>? LastRebuildBatch;
        public IReadOnlyCollection<MemorySearchResult> SearchResults = Array.Empty<MemorySearchResult>();

        public Task IndexAsync(Memory memory) { Indexed.Add(memory); return Task.CompletedTask; }
        public Task RemoveAsync(MemoryId id) { Removed.Add(id); return Task.CompletedTask; }
        public Task RebuildAsync(IEnumerable<Memory> memories) { LastRebuildBatch = memories; return Task.CompletedTask; }
        public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query) => Task.FromResult(SearchResults);
    }

    private static Memory NewMemory(string content = "test", MemoryType type = MemoryType.Fact) =>
        new(type, content);
    // ... [test methods]
}
```

- [ ] **Step 2: Run tests**
Run: `dotnet test tests/Eling.Application.Tests/Eling.Application.Tests.csproj`

- [ ] **Step 3: Run the full test suite**
Run: `dotnet test Eling.slnx` — all existing tests must stay green (Storage, Index, Core).

- [ ] **Step 4: Commit**
Commit message: `feat: add application/use-case layer (Pecut 5)`

---

## Definition of Done

- [ ] `dotnet build Eling.slnx` succeeds.
- [ ] `dotnet test Eling.slnx` succeeds (new + existing tests).
- [ ] `Eling.Mcp` and `Eling.Server` are NOT yet modified (they stay on their old project references until their own pecuts).
- [ ] No new NuGet packages added.
