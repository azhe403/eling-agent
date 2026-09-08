# Smart Save & Memory Maintenance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Menambahkan kapabilitas Smart Save (local fuzzy match & surgical merge saat `SaveAsync`) dan on-demand Memory Maintenance (sweep & konsolidasi) di backend Eling.

**Architecture:** Menggunakan pure C# deterministic tokenization + Jaccard similarity / n-gram token overlap (`MemorySimilarity`) untuk matching fuzzy tanpa AI/LLM berat. Mengintegrasikannya ke dalam `MemoryService.SaveAsync` (Smart Save) serta membangun `MemoryMaintenanceService` + MCP tool `memory_maintenance` + REST API untuk konsolidasi berkala.

**Tech Stack:** C# .NET 9, SQLite FTS5 (`Eling.Core`), Minimal API & MCP SDK (`Eling.Backend`), xUnit (`Eling.Core.Tests`, `Eling.Backend.Tests`).

## Global Constraints

- Never commit/push/amend/reset without explicit user instruction.
- Chat in Indonesian/English as per AGENTS.md, code and docs in English.
- Unit tests: per csproj (`dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test`), NEVER solution-wide or chained.
- Lightweight: No external LLM/embeddings overhead; pure deterministic offline string/token matching.
- Safe merges: Oldest ULID survives, union of tags, non-destructive (absorbed memories become `Superseded`).

---

### Task 1: Implement `MemorySimilarity` (Deterministic Tokenization & Jaccard)

**Files:**
- Create: `src/backend/Eling.Core/Memory/MemorySimilarity.cs`
- Test: `tests/Eling.Core.Tests/MemorySimilarityTests.cs`

**Interfaces:**
- Produces: `MemorySimilarity.Normalize(string content) -> HashSet<string>`, `MemorySimilarity.CalculateJaccard(string a, string b) -> double`

- [ ] **Step 1: Write the failing test**

```csharp
using Eling.Core;
using Xunit;

namespace Eling.Core.Tests;

public class MemorySimilarityTests
{
    [Fact]
    public void CalculateJaccard_IdenticalStrings_ReturnsOne()
    {
        var score = MemorySimilarity.CalculateJaccard("Always check git status before commit", "Always check git status before commit");
        Assert.Equal(1.0, score, precision: 4);
    }

    [Fact]
    public void CalculateJaccard_SlightlyDifferent_ReturnsHighOverlap()
    {
        var score = MemorySimilarity.CalculateJaccard(
            "Always check git status before commit",
            "Always check git status and diff before commit"
        );
        Assert.True(score > 0.7);
    }

    [Fact]
    public void CalculateJaccard_CompletelyDifferent_ReturnsZeroOrLow()
    {
        var score = MemorySimilarity.CalculateJaccard("C# dotnet build configuration", "Python fastapi rest endpoint");
        Assert.True(score < 0.1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter MemorySimilarityTests --artifacts-path .bin-test`
Expected: FAIL (Compilation error: `MemorySimilarity` does not exist).

- [ ] **Step 3: Implement `MemorySimilarity`**

```csharp
using System.Text.RegularExpressions;

namespace Eling.Core;

public static class MemorySimilarity
{
    private static readonly Regex TokenSplitRegex = new(@"[\s\p{P}]+", RegexOptions.Compiled);

    public static HashSet<string> Tokenize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var tokens = TokenSplitRegex.Split(content.Trim().ToLowerInvariant())
            .Where(t => t.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return tokens;
    }

    public static double CalculateJaccard(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);

        if (setA.Count == 0 && setB.Count == 0)
            return 1.0;
        if (setA.Count == 0 || setB.Count == 0)
            return 0.0;

        var intersectionCount = setA.Intersect(setB).Count();
        var unionCount = setA.Union(setB).Count();

        return (double)intersectionCount / unionCount;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter MemorySimilarityTests --artifacts-path .bin-test`
Expected: PASS

---

### Task 2: Enhance `MemoryService.SaveAsync` with Smart Save (Fuzzy Match & Merge)

**Files:**
- Modify: `src/backend/Eling.Core/Memory/MemoryService.cs`
- Test: `tests/Eling.Core.Tests/MemoryServiceTests.cs` (or create if needed)

**Interfaces:**
- Consumes: `MemorySimilarity.CalculateJaccard`
- Produces: Enhanced `SaveAsync(Memory memory)` which updates/enriches when Jaccard >= 0.85 for active memories of the same type.

- [ ] **Step 1: Write test for fuzzy save merge**

```csharp
[Fact]
public async Task SaveAsync_FuzzySimilarActiveMemory_MergesAndUpdatesExisting()
{
    var storage = new InMemoryStorage();
    var index = new InMemoryIndex();
    var service = new MemoryService(storage, index);

    var initial = new Memory(MemoryType.Preference, "Selalu pakai question tool untuk minta approval sebelum eksekusi", ["workflow"]);
    var firstResult = await service.SaveAsync(initial);
    Assert.Equal(SaveAction.Created, firstResult.Action);

    // Save slightly extended version of the same thought
    var incoming = new Memory(MemoryType.Preference, "Selalu gunakan question tool untuk meminta approval sebelum eksekusi bash/write", ["preference", "approval"]);
    var secondResult = await service.SaveAsync(incoming);

    Assert.Equal(SaveAction.Updated, secondResult.Action);
    Assert.Equal(firstResult.Memory.Id, secondResult.Memory.Id); // Kept existing ID
    Assert.Contains("approval", secondResult.Memory.Tags);
    Assert.Contains("workflow", secondResult.Memory.Tags);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter MemoryServiceTests --artifacts-path .bin-test`
Expected: FAIL (returns `Created` instead of `Updated` because strings are not exact match).

- [ ] **Step 3: Update `MemoryService.SaveAsync` logic**

In `MemoryService.cs`, update `FindActiveSimilarAsync`:
Check exact match first (normalized string equals), if not found, check if any active memory of same type has `MemorySimilarity.CalculateJaccard(existing.Content, incoming.Content) >= 0.85`.
If found, call `MergeIntoAsync(existing, incoming)`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter MemoryServiceTests --artifacts-path .bin-test`
Expected: PASS

---

### Task 3: Implement `MemoryMaintenanceService` (Sweep, Dedup, Merge, Reconcile)

**Files:**
- Create: `src/backend/Eling.Core/Memory/IMemoryMaintenanceService.cs`
- Create: `src/backend/Eling.Core/Memory/MaintenanceModels.cs`
- Create: `src/backend/Eling.Core/Memory/MemoryMaintenanceService.cs`
- Test: `tests/Eling.Core.Tests/MemoryMaintenanceServiceTests.cs`

**Interfaces:**
- Produces: `IMemoryMaintenanceService.RunAsync(MaintenanceRequest request) -> Task<MaintenanceReport>`

- [ ] **Step 1: Write test for dryRun maintenance detection**

```csharp
[Fact]
public async Task RunAsync_DryRun_DetectsFuzzyDuplicatesWithoutMutating()
{
    // Setup test with 2 similar active memories
    // Run maintenance dryRun: true
    // Assert finding count == 1, proposedAction == "merge"
    // Assert neither memory status changed to Superseded
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter MemoryMaintenanceServiceTests --artifacts-path .bin-test`
Expected: FAIL

- [ ] **Step 3: Implement `MemoryMaintenanceService`**

Implement full pipeline according to `docs/superpowers/specs/2026-09-01-memory-maintenance-design.md`:
- `detect`: scan active memories, group duplicates (exact & fuzzy >= threshold).
- `apply`: if `dryRun: false`, survivor keeps oldest ULID, merged tags/sources, sets absorbed memory to `Superseded`.

- [ ] **Step 4: Run tests to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter MemoryMaintenanceServiceTests --artifacts-path .bin-test`
Expected: PASS

---

### Task 4: Expose `memory_maintenance` MCP Tool & Endpoint in Backend

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/MemoryMaintenanceTool.cs`
- Create: `src/backend/Eling.Backend/Endpoints/MemoryMaintenanceEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs`
- Test: `tests/Eling.Backend.Tests/MemoryMaintenanceToolTests.cs`

- [ ] **Step 1: Write MCP tool test**

```csharp
[Fact]
public async Task MemoryMaintenanceTool_ExecutesDryRunReport()
{
    // Verify tool calls maintenance service and returns json report
}
```

- [ ] **Step 2: Implement Tool, Endpoints, and DI Registration**

- Add `MemoryMaintenanceTool` under `Mcp/Tools/`.
- Add route mapping `app.MapPost("/api/memory/maintenance", ...)` in `MemoryMaintenanceEndpoints`.
- Register in `McpServiceExtensions` and `DashboardServices`.

- [ ] **Step 3: Run Backend unit tests**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: PASS

---

### Task 5: Verification & Smoke Test

**Files:**
- Test: Full build & test suite execution

- [ ] **Step 1: Run full test suite for Core and Backend**

Run:
1. `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test`
2. `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: All tests pass.

- [ ] **Step 2: Check git status hygiene**

Run: `git status --short`
Expected: Clean working tree changes matching expected plan files.
