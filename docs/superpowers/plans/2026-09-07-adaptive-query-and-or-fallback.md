# Adaptive Query — AND → OR Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When an FTS5 AND-query yields < 3 hits, automatically retry with OR-queries on both porter and trigram layers, and expose the resulting `QueryMode` to the agent via the MCP response.

**Architecture:** Refactor `SqliteMemoryIndex.SearchAsync` to a two-phase orchestration: run existing AND-queries first, check unique-id count against threshold (3), and only fall back to OR-queries if the AND phase didn't hit the baseline. Propagate a new `QueryMode` field ("and" | "or-fallback") through `MemorySearchResult` → `ScopedSearchResult` → `MemoryRecallHit` → `MemoryRecallMemory` DTO. Field is additive (default `null`) so all 13+ existing call sites compile unchanged.

**Tech Stack:** .NET 10 / C# 13, SQLite FTS5, xUnit.

## Global Constraints

- **Threshold:** Hardcoded to 3 (matches recall Quality Gate in `ServerInstructions.cs`). No configurability.
- **QueryMode granularity:** Per-hit, not per-response. Each `MemorySearchResult` carries the mode of the branch that produced it.
- **Backward compatibility:** `QueryMode` is optional in every type it touches. Existing positional constructors still work via C# default-parameter semantics.
- **Scoring:** Unchanged. Same `porter 1.0 + trigram 0.4` formula.
- **Logging:** `MemoryRecallService` logs at `Debug` level when fallback fires (per-call: once per fallback event, with token count + topic summary).
- **Project:** Eling (Windows, .NET 10, xUnit, per-csproj test isolation).
- **Build/test command:** `dotnet test <csproj> --artifacts-path .bin-test` for tests; `dotnet build <csproj> -p:ElingSkipDashboard=true --artifacts-path .bin` for the binary.

---

### Task 1: Add `QueryMode` field to `MemorySearchResult`

**Files:**
- Modify: `src/backend/Eling.Core/Memory/MemorySearchResult.cs`

**Interfaces:**
- Consumes: none
- Produces: `public readonly record struct MemorySearchResult(MemoryId Id, double Rank, IReadOnlyCollection<string>? MatchedVia = null, double PorterScore = 0.0, double TrigramScore = 0.0, string? QueryMode = null)`

- [ ] **Step 1: Edit the file to add the `QueryMode` field**

Replace the entire file content with:

```csharp
using Eling.Core;

namespace Eling.Core;

public readonly record struct MemorySearchResult(
    MemoryId Id,
    double Rank,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null);
```

- [ ] **Step 2: Build Core to verify the additive change compiles**

Run from `C:\some-folder\Eling`:
```bash
dotnet build src/backend/Eling.Core/Eling.Core.csproj --artifacts-path .bin
```

Expected: 0 errors. The pre-existing `IMemoryService.cs(2,7): warning CS0105` may appear; ignore it.

- [ ] **Step 3: Commit**

```bash
git add src/backend/Eling.Core/Memory/MemorySearchResult.cs
git commit -m "feat(memory): add QueryMode field to MemorySearchResult"
```

---

### Task 2: Add AND- and OR- query builders + adaptive `SearchAsync` orchestration

**Files:**
- Modify: `src/backend/Eling.Core/Memory/Storage/SqliteMemoryIndex.cs`

**Interfaces:**
- Consumes: `MemorySearchResult` with `QueryMode` (Task 1)
- Produces:
  - `private static string BuildPorterAndQuery(string[] tokens)`
  - `private static string BuildPorterOrQuery(string[] tokens)`
  - `private static string BuildTrigramAndQuery(string[] tokens)`
  - `private static string BuildTrigramOrQuery(string[] tokens)`
  - `public async Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)` — refactored to AND-then-OR with threshold = 3

- [ ] **Step 1: Rename existing `BuildPorterQuery` to `BuildPorterAndQuery` and add `BuildPorterOrQuery`**

Find this in `SqliteMemoryIndex.cs`:

```csharp
private static string BuildPorterQuery(string[] tokens)
{
    var phrases = new List<string>();
    foreach (var token in tokens)
    {
        if (token.Length >= 2)
        {
            phrases.Add($"\"{token}\"");
        }
    }
    return string.Join(' ', phrases);
}
```

Replace with:

```csharp
private static string BuildPorterAndQuery(string[] tokens)
{
    var phrases = new List<string>();
    foreach (var token in tokens)
    {
        if (token.Length >= 2)
        {
            phrases.Add($"\"{token}\"");
        }
    }
    return string.Join(' ', phrases);
}

private static string BuildPorterOrQuery(string[] tokens)
{
    var phrases = new List<string>();
    foreach (var token in tokens)
    {
        if (token.Length >= 2)
        {
            phrases.Add($"\"{token}\"");
        }
    }
    return string.Join(" OR ", phrases);
}
```

- [ ] **Step 2: Rename existing `BuildTrigramQuery` to `BuildTrigramAndQuery` and add `BuildTrigramOrQuery`**

Find this in `SqliteMemoryIndex.cs`:

```csharp
private static string BuildTrigramQuery(string[] tokens)
{
    // Trigram tokenizer: each token becomes a substring to search.
    // Short tokens (< 3 chars) cannot form trigrams and are skipped.
    var phrases = new List<string>();
    foreach (var token in tokens)
    {
        if (token.Length >= 3)
        {
            phrases.Add($"\"{token}\"");
        }
    }
    return string.Join(' ', phrases);
}
```

Replace with:

```csharp
private static string BuildTrigramAndQuery(string[] tokens)
{
    // Trigram tokenizer: each token becomes a substring to search.
    // Short tokens (< 3 chars) cannot form trigrams and are skipped.
    var phrases = new List<string>();
    foreach (var token in tokens)
    {
        if (token.Length >= 3)
        {
            phrases.Add($"\"{token}\"");
        }
    }
    return string.Join(' ', phrases);
}

private static string BuildTrigramOrQuery(string[] tokens)
{
    var phrases = new List<string>();
    foreach (var token in tokens)
    {
        if (token.Length >= 3)
        {
            phrases.Add($"\"{token}\"");
        }
    }
    return string.Join(" OR ", phrases);
}
```

- [ ] **Step 3: Refactor `SearchAsync` to use AND-then-OR orchestration**

Find this in `SqliteMemoryIndex.cs` (after Steps 1 and 2 renamed the builders):

```csharp
public async Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(query);

    var tokens = ExtractTokens(query);
    if (tokens.Length == 0)
    {
        return Array.Empty<MemorySearchResult>();
    }

    // Layer 1: Porter stem query (root-form matching)
    var porterQuery = BuildPorterAndQuery(tokens);
    // Layer 2: Trigram query (substring/typo matching)
    var trigramQuery = BuildTrigramAndQuery(tokens);

    var porterResults = porterQuery.Length > 0
        ? await SearchPorterAsync(porterQuery)
        : Array.Empty<(string Id, double Score)>();
    var trigramResults = trigramQuery.Length > 0
        ? await SearchTrigramAsync(trigramQuery)
        : Array.Empty<(string Id, double Score)>();

    return MergeRankings(porterResults, trigramResults);
}
```

Replace with:

```csharp
private const int AndToOrThreshold = 3;

public async Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(query);

    var tokens = ExtractTokens(query);
    if (tokens.Length == 0)
    {
        return Array.Empty<MemorySearchResult>();
    }

    // Phase 1: AND-queries on porter and trigram. Default precision.
    var porterAndQuery = BuildPorterAndQuery(tokens);
    var trigramAndQuery = BuildTrigramAndQuery(tokens);

    var porterAnd = porterAndQuery.Length > 0
        ? await SearchPorterAsync(porterAndQuery)
        : Array.Empty<(string Id, double Score)>();
    var trigramAnd = trigramAndQuery.Length > 0
        ? await SearchTrigramAsync(trigramAndQuery)
        : Array.Empty<(string Id, double Score)>();

    var andMode = MergeRankings(porterAnd, trigramAnd, "and");
    if (andMode.Count >= AndToOrThreshold)
    {
        return andMode;
    }

    // Phase 2: OR-queries. Falls back when AND yields < threshold unique ids,
    // which happens when the agent submits a broad associative topic list and
    // no single memory contains all of them. BM25 ranking still surfaces the
    // most cross-relevant memories at the top of the OR results.
    var porterOrQuery = BuildPorterOrQuery(tokens);
    var trigramOrQuery = BuildTrigramOrQuery(tokens);

    var porterOr = porterOrQuery.Length > porterAndQuery.Length
        ? await SearchPorterAsync(porterOrQuery)
        : Array.Empty<(string Id, double Score)>();
    var trigramOr = trigramOrQuery.Length > trigramAndQuery.Length
        ? await SearchTrigramAsync(trigramOrQuery)
        : Array.Empty<(string Id, double Score)>();

    return MergeRankings(porterOr, trigramOr, "or-fallback");
}
```

- [ ] **Step 4: Refactor `MergeRankings` to accept a `queryMode` parameter**

Find this in `SqliteMemoryIndex.cs`:

```csharp
private static IReadOnlyCollection<MemorySearchResult> MergeRankings(
    IReadOnlyCollection<(string Id, double Score)> porter,
    IReadOnlyCollection<(string Id, double Score)> trigram)
{
    // Porter is primary signal (semantic match); trigram is secondary (typo/substring).
    // Weight: porter x 1.0, trigram x 0.4. Deduplicate by id, sum scores, sort descending.
    var totals = new Dictionary<string, double>(StringComparer.Ordinal);
    var porterById = new Dictionary<string, double>(StringComparer.Ordinal);
    var trigramById = new Dictionary<string, double>(StringComparer.Ordinal);

    foreach (var (id, score) in porter)
    {
        // BM25 returns negative ranks; more-negative = better. Invert so higher = better.
        var positive = -score;
        porterById[id] = positive;
        totals[id] = totals.GetValueOrDefault(id) + positive;
    }
    foreach (var (id, score) in trigram)
    {
        var weighted = (-score) * 0.4;
        trigramById[id] = trigramById.GetValueOrDefault(id) + weighted;
        totals[id] = totals.GetValueOrDefault(id) + weighted;
    }

    return totals
        .OrderByDescending(kv => kv.Value)
        .Select(kv =>
        {
            var layers = new List<string>(2);
            if (porterById.ContainsKey(kv.Key)) layers.Add("porter");
            if (trigramById.ContainsKey(kv.Key)) layers.Add("trigram");
            return new MemorySearchResult(
                new MemoryId(kv.Key),
                kv.Value,
                layers,
                porterById.GetValueOrDefault(kv.Key),
                trigramById.GetValueOrDefault(kv.Key));
        })
        .ToList();
}
```

Replace with:

```csharp
private static IReadOnlyCollection<MemorySearchResult> MergeRankings(
    IReadOnlyCollection<(string Id, double Score)> porter,
    IReadOnlyCollection<(string Id, double Score)> trigram,
    string queryMode)
{
    // Porter is primary signal (semantic match); trigram is secondary (typo/substring).
    // Weight: porter x 1.0, trigram x 0.4. Deduplicate by id, sum scores, sort descending.
    var totals = new Dictionary<string, double>(StringComparer.Ordinal);
    var porterById = new Dictionary<string, double>(StringComparer.Ordinal);
    var trigramById = new Dictionary<string, double>(StringComparer.Ordinal);

    foreach (var (id, score) in porter)
    {
        // BM25 returns negative ranks; more-negative = better. Invert so higher = better.
        var positive = -score;
        porterById[id] = positive;
        totals[id] = totals.GetValueOrDefault(id) + positive;
    }
    foreach (var (id, score) in trigram)
    {
        var weighted = (-score) * 0.4;
        trigramById[id] = trigramById.GetValueOrDefault(id) + weighted;
        totals[id] = totals.GetValueOrDefault(id) + weighted;
    }

    return totals
        .OrderByDescending(kv => kv.Value)
        .Select(kv =>
        {
            var layers = new List<string>(2);
            if (porterById.ContainsKey(kv.Key)) layers.Add("porter");
            if (trigramById.ContainsKey(kv.Key)) layers.Add("trigram");
            return new MemorySearchResult(
                new MemoryId(kv.Key),
                kv.Value,
                layers,
                porterById.GetValueOrDefault(kv.Key),
                trigramById.GetValueOrDefault(kv.Key),
                queryMode);
        })
        .ToList();
}
```

- [ ] **Step 5: Build Core to verify the refactor compiles**

Run from `C:\some-folder\Eling`:
```bash
dotnet build src/backend/Eling.Core/Eling.Core.csproj --artifacts-path .bin
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/backend/Eling.Core/Memory/Storage/SqliteMemoryIndex.cs
git commit -m "feat(memory): adaptive AND->OR fallback with QueryMode in SearchAsync"
```

---

### Task 3: Add `QueryMode` to `MemoryRecallHit`

**Files:**
- Modify: `src/backend/Eling.Core/MemoryRecall/MemoryRecallHit.cs`

**Interfaces:**
- Consumes: `MemorySearchResult.QueryMode` (Task 1)
- Produces: `public sealed record MemoryRecallHit(Memory Memory, IReadOnlyCollection<string>? MatchedVia = null, double PorterScore = 0.0, double TrigramScore = 0.0, string? QueryMode = null)`

- [ ] **Step 1: Edit the file**

Replace the entire file content with:

```csharp
namespace Eling.Core;

/// <summary>
/// A recalled memory with the search-layer metadata that produced it:
/// which FTS layers matched (<c>porter</c> / <c>trigram</c>), the
/// per-layer BM25-derived scores, and the query mode ("and" or
/// "or-fallback") that produced the hit. Populated by the recall
/// service so the MCP response can show why a memory surfaced for the
/// given topics and which query strategy was used.
/// </summary>
public sealed record MemoryRecallHit(
    Memory Memory,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null);
```

- [ ] **Step 2: Build Core to verify the additive change compiles**

Run from `C:\some-folder\Eling`:
```bash
dotnet build src/backend/Eling.Core/Eling.Core.csproj --artifacts-path .bin
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/backend/Eling.Core/MemoryRecall/MemoryRecallHit.cs
git commit -m "feat(memory): add QueryMode to MemoryRecallHit"
```

---

### Task 4: Propagate `QueryMode` through `ScopedSearchResult` and `ScopedMemoryService`

**Files:**
- Modify: `src/backend/Eling.Core/Memory/ScopedMemory.cs`
- Modify: `src/backend/Eling.Core/Memory/ScopedMemoryService.cs`

**Interfaces:**
- Consumes: `MemorySearchResult.QueryMode` (Task 1)
- Produces: `ScopedSearchResult` carries `QueryMode`; `ScopedMemoryService.SearchAsync` propagates it to callers.

- [ ] **Step 1: Add `QueryMode` to `ScopedSearchResult`**

In `src/backend/Eling.Core/Memory/ScopedMemory.cs`, find the existing record:

```csharp
public sealed record ScopedSearchResult(
    MemoryId Id,
    double Rank,
    MemoryScopeKind Scope,
    string? ProjectRoot = null,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0);
```

Replace with:

```csharp
public sealed record ScopedSearchResult(
    MemoryId Id,
    double Rank,
    MemoryScopeKind Scope,
    string? ProjectRoot = null,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null);
```

- [ ] **Step 2: Propagate `QueryMode` in `ScopedMemoryService.SearchAsync` project-scope branch**

In `src/backend/Eling.Core/Memory/ScopedMemoryService.cs`, find:

```csharp
        if (normalizedScope == "project")
        {
            merged = projectResults.Select(r => new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Project, _projectRoot, r.MatchedVia, r.PorterScore, r.TrigramScore)).ToList().AsReadOnly();
        }
```

Replace with:

```csharp
        if (normalizedScope == "project")
        {
            merged = projectResults.Select(r => new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Project, _projectRoot, r.MatchedVia, r.PorterScore, r.TrigramScore, r.QueryMode)).ToList().AsReadOnly();
        }
```

- [ ] **Step 3: Propagate `QueryMode` in `ScopedMemoryService.SearchAsync` global-scope branch**

In `src/backend/Eling.Core/Memory/ScopedMemoryService.cs`, find:

```csharp
        else if (normalizedScope == "global")
        {
            merged = globalResults.Select(r => new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Global, null, r.MatchedVia, r.PorterScore, r.TrigramScore)).ToList().AsReadOnly();
        }
```

Replace with:

```csharp
        else if (normalizedScope == "global")
        {
            merged = globalResults.Select(r => new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Global, null, r.MatchedVia, r.PorterScore, r.TrigramScore, r.QueryMode)).ToList().AsReadOnly();
        }
```

- [ ] **Step 4: Propagate `QueryMode` in `MemoryMerger.MergeSearchResults`**

In `src/backend/Eling.Core/Memory/MemoryMerger.cs`, find the two `new ScopedSearchResult(...)` constructions in the loop:

```csharp
            merged.Add(new ScopedSearchResult(r.Id, boostedRank, MemoryScopeKind.Project, projectRoot, r.MatchedVia, r.PorterScore, r.TrigramScore));
```

```csharp
            merged.Add(new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Global, null, r.MatchedVia, r.PorterScore, r.TrigramScore));
```

Replace with:

```csharp
            merged.Add(new ScopedSearchResult(r.Id, boostedRank, MemoryScopeKind.Project, projectRoot, r.MatchedVia, r.PorterScore, r.TrigramScore, r.QueryMode));
```

```csharp
            merged.Add(new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Global, null, r.MatchedVia, r.PorterScore, r.TrigramScore, r.QueryMode));
```

- [ ] **Step 5: Build Core**

Run from `C:\some-folder\Eling`:
```bash
dotnet build src/backend/Eling.Core/Eling.Core.csproj --artifacts-path .bin
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/backend/Eling.Core/Memory/ScopedMemory.cs \
        src/backend/Eling.Core/Memory/ScopedMemoryService.cs \
        src/backend/Eling.Core/Memory/MemoryMerger.cs
git commit -m "feat(memory): propagate QueryMode through ScopedSearchResult"
```

---

### Task 5: Propagate `QueryMode` in `MemoryRecallService` to `MemoryRecallHit` and add fallback log

**Files:**
- Modify: `src/backend/Eling.Core/MemoryRecall/MemoryRecallService.cs`

**Interfaces:**
- Consumes: `ScopedSearchResult.QueryMode` (Task 4)
- Produces: `MemoryRecallHit.QueryMode` populated from the underlying search hit; `ILogger?.LogDebug` line emitted when fallback fires.

- [ ] **Step 1: Add logger to `MemoryRecallService` constructor (if not already present)**

In `src/backend/Eling.Core/MemoryRecall/MemoryRecallService.cs`, find:

```csharp
public sealed class MemoryRecallService : IMemoryRecallService
{
    private readonly IScopedMemoryService _scoped;
    private readonly IIntentionStorage _intentions;

    public MemoryRecallService(IScopedMemoryService scoped, IIntentionStorage intentions)
    {
        _scoped = scoped;
        _intentions = intentions;
    }
```

Replace with:

```csharp
public sealed class MemoryRecallService : IMemoryRecallService
{
    private readonly IScopedMemoryService _scoped;
    private readonly IIntentionStorage _intentions;
    private readonly ILogger<MemoryRecallService>? _logger;

    public MemoryRecallService(
        IScopedMemoryService scoped,
        IIntentionStorage intentions,
        ILogger<MemoryRecallService>? logger = null)
    {
        _scoped = scoped;
        _intentions = intentions;
        _logger = logger;
    }
```

Add the using directive at the top of the file if not present:

```csharp
using Microsoft.Extensions.Logging;
```

- [ ] **Step 2: Populate `QueryMode` on each `MemoryRecallHit` and emit a debug log when fallback fires**

In the same file, find this single block:

```csharp
                    if (scoped is not null)
                    {
                        recall.Add(new MemoryRecallHit(
                            scoped.Memory,
                            hit.MatchedVia,
                            hit.PorterScore,
                            hit.TrigramScore));
                    }
```

Replace it with this expanded version that adds the `QueryMode` field to the `MemoryRecallHit` constructor and emits a debug log when the AND-then-OR pipeline fell back:

```csharp
                    if (hit.QueryMode == "or-fallback")
                    {
                        _logger?.LogDebug(
                            "memory_recall fell back to OR queries: tokens={TokenCount}, scope={Scope}",
                            topics.Count, scope);
                    }
                    if (scoped is not null)
                    {
                        recall.Add(new MemoryRecallHit(
                            scoped.Memory,
                            hit.MatchedVia,
                            hit.PorterScore,
                            hit.TrigramScore,
                            hit.QueryMode));
                    }
```

- [ ] **Step 3: Build Core to verify the change compiles**

Run from `C:\some-folder\Eling`:
```bash
dotnet build src/backend/Eling.Core/Eling.Core.csproj --artifacts-path .bin
```

Expected: 0 errors.

- [ ] **Step 4: Run all Core tests to verify zero regression**

```bash
dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test
```

Expected: 68/68 pass (no new tests yet — just verify the propagation change didn't break existing tests).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/MemoryRecall/MemoryRecallService.cs
git commit -m "feat(memory): propagate QueryMode to MemoryRecallHit + debug log on fallback"
```

---

### Task 6: Add `queryMode` JSON field to `MemoryRecallMemory` DTO

**Files:**
- Modify: `src/backend/Eling.Backend/Dtos/MemoryRecallMemory.cs`

**Interfaces:**
- Consumes: `MemoryRecallHit.QueryMode` (Task 3)
- Produces: `[JsonPropertyName("queryMode")] public string? QueryMode { get; set; }` and the setter path in `From(MemoryRecallHit)`.

- [ ] **Step 1: Add the `QueryMode` property**

In `src/backend/Eling.Backend/Dtos/MemoryRecallMemory.cs`, find the existing `trigramScore` block:

```csharp
    [JsonPropertyName("trigramScore")]
    public double TrigramScore { get; set; }
```

Replace with:

```csharp
    [JsonPropertyName("trigramScore")]
    public double TrigramScore { get; set; }

    [JsonPropertyName("queryMode")]
    public string? QueryMode { get; set; }
```

- [ ] **Step 2: Set `QueryMode` in the `From(MemoryRecallHit)` overload**

Find this:

```csharp
    public static MemoryRecallMemory From(MemoryRecallHit hit)
    {
        var dto = From(hit.Memory);
        dto.MatchedVia = hit.MatchedVia;
        dto.PorterScore = hit.PorterScore;
        dto.TrigramScore = hit.TrigramScore;
        return dto;
    }
```

Replace with:

```csharp
    public static MemoryRecallMemory From(MemoryRecallHit hit)
    {
        var dto = From(hit.Memory);
        dto.MatchedVia = hit.MatchedVia;
        dto.PorterScore = hit.PorterScore;
        dto.TrigramScore = hit.TrigramScore;
        dto.QueryMode = hit.QueryMode;
        return dto;
    }
```

- [ ] **Step 3: Build Backend to verify the DTO change compiles**

Run from `C:\some-folder\Eling`:
```bash
dotnet build src/backend/Eling.Backend/Eling.Backend.csproj -p:ElingSkipDashboard=true --artifacts-path .bin
```

Expected: 0 errors.

- [ ] **Step 4: Run all Backend tests to verify zero regression**

```bash
dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test
```

Expected: 124/124 pass (no new tests yet).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Backend/Dtos/MemoryRecallMemory.cs
git commit -m "feat(memory): add queryMode JSON field to MemoryRecallMemory DTO"
```

---

### Task 7: Add Core tests covering AND-hit-enough, AND-too-few → OR fallback, and `QueryMode` propagation

**Files:**
- Modify: `tests/Eling.Core.Tests/FtsSearchLayerTests.cs`

**Interfaces:**
- Consumes: `SqliteMemoryIndex.SearchAsync` with adaptive AND→OR behavior (Task 2)
- Produces: 5 new xUnit tests that pin the AND-OR threshold, mode propagation, and per-layer merge ordering

- [ ] **Step 1: Append the new test class section at the bottom of `FtsSearchLayerTests.cs`**

Open `tests/Eling.Core.Tests/FtsSearchLayerTests.cs`. Find the very last `}` character of the test class (after the `Rank_ReflectsWeightedSumOfLayerScores` test). Replace that closing brace plus the file's last newline with the new tests appended before it. Concretely, the final lines of the file currently look like this:

```csharp
            Assert.Equal(expectedRank, hit.Rank, precision: 5);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
```

Replace the final `}` (class-closing) so the file ends with the existing test's `finally` block followed by a new test method and a final class-closing `}`:

```csharp
            Assert.Equal(expectedRank, hit.Rank, precision: 5);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AndQuery_ResultsAtOrAboveThreshold_ReturnsAndMode()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Three memories all sharing the token "git": AND query for
            // ['git', 'commit'] will literally contain both tokens in all
            // three, producing >= 3 unique ids.
            var a = NewMemory("alpha git commit", "git");
            var b = NewMemory("bravo git commit", "git");
            var c = NewMemory("charlie git commit", "git");
            await index.IndexAsync(a);
            await index.IndexAsync(b);
            await index.IndexAsync(c);

            var results = await index.SearchAsync("git commit");

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal("and", r.QueryMode));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AndQuery_BelowThreshold_FallsBackToOrWithOrFallbackMode()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Three memories each contain only ONE of the three tokens.
            // An AND query would return 0; OR should return all 3.
            var a = NewMemory("alpha", "git");
            var b = NewMemory("bravo", "commit");
            var c = NewMemory("charlie", "hygiene");
            await index.IndexAsync(a);
            await index.IndexAsync(b);
            await index.IndexAsync(c);

            var results = await index.SearchAsync("git commit hygiene");

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal("or-fallback", r.QueryMode));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AndQuery_ExactlyTwoResults_TriggersOrFallback()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Two memories that share the token "git" with "commit"; the
            // third is unrelated. AND of ['git', 'commit'] returns 2 (< 3),
            // so fallback to OR fires.
            var a = NewMemory("alpha git commit", "git");
            var b = NewMemory("bravo git commit", "git");
            var c = NewMemory("charlie unrelated", "unrelated");
            await index.IndexAsync(a);
            await index.IndexAsync(b);
            await index.IndexAsync(c);

            var results = await index.SearchAsync("git commit");

            Assert.Equal(3, results.Count);
            Assert.Contains(results, r => r.Id == a.Id);
            Assert.Contains(results, r => r.Id == b.Id);
            Assert.Contains(results, r => r.Id == c.Id);
            Assert.All(results, r => Assert.Equal("or-fallback", r.QueryMode));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AndQuery_SingleSharedToken_ReturnsAndMode()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Two-token query where both tokens appear in the same memory
            // — AND should hit and not fall back.
            var only = NewMemory("git commit hygiene", "git");
            await index.IndexAsync(only);

            var results = await index.SearchAsync("git commit");

            Assert.Single(results);
            Assert.Equal("and", results.First().QueryMode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OrFallback_PorterOnlyHit_CarriesMatchedViaPorter()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Single-word Porter-stemmable memory; query that stems to
            // a different form to force OR-only path through porter layer.
            var only = NewMemory("running", "hygiene");
            await index.IndexAsync(only);

            var results = await index.SearchAsync("runner hygiene");

            // "runner" doesn't stem to "running" (they are different roots)
            // so the AND-query needs OR fallback. We just assert the mode
            // is consistent across all returned hits and at least one hit
            // carries the porter layer.
            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.Equal("or-fallback", r.QueryMode));
            Assert.Contains(results, r => r.MatchedVia!.Contains("porter"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run the new Core tests to verify they pass**

Run from `C:\some-folder\Eling`:
```bash
dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~AndQuery|FullyQualifiedName~OrFallback"
```

Expected: 5/5 pass.

- [ ] **Step 3: Run the full Core test suite to verify zero regression**

```bash
dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test
```

Expected: 73/73 pass (68 existing + 5 new).

- [ ] **Step 4: Commit**

```bash
git add tests/Eling.Core.Tests/FtsSearchLayerTests.cs
git commit -m "test(memory): add AND->OR fallback and QueryMode propagation tests"
```

---

### Task 8: Add Backend DTO tests for `queryMode` field

**Files:**
- Modify: `tests/Eling.Backend.Tests/MemoryRecallDtosTests.cs`

**Interfaces:**
- Consumes: `MemoryRecallMemory.QueryMode` (Task 6)
- Produces: 2 new xUnit tests for `queryMode` propagation and JSON round-trip

- [ ] **Step 1: Append the new tests at the bottom of `MemoryRecallDtosTests.cs`**

Open `tests/Eling.Backend.Tests/MemoryRecallDtosTests.cs`. The last `}` is the class-closing brace. Insert these two tests just before it:

```csharp
    [Fact]
    public void From_MemoryRecallHit_CarriesQueryMode()
    {
        var memory = new Memory(MemoryType.Fact, "Test content");
        var hit = new MemoryRecallHit(
            memory,
            MatchedVia: new[] { "porter", "trigram" },
            PorterScore: 1.2,
            TrigramScore: 0.8,
            QueryMode: "or-fallback");

        var dto = MemoryRecallMemory.From(hit);

        Assert.Equal("or-fallback", dto.QueryMode);
    }

    [Fact]
    public void From_PlainMemory_LeavesQueryModeNull()
    {
        var memory = new Memory(MemoryType.Fact, "Test content");

        var dto = MemoryRecallMemory.From(memory);

        Assert.Null(dto.QueryMode);
    }

    [Fact]
    public void Serialize_MemoryRecallMemory_IncludesQueryModeJsonProperty()
    {
        var memory = new Memory(MemoryType.Fact, "Test content");
        var dto = MemoryRecallMemory.From(new MemoryRecallHit(
            memory,
            MatchedVia: new[] { "porter" },
            PorterScore: 0.9,
            TrigramScore: 0.0,
            QueryMode: "or-fallback"));

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"queryMode\":\"or-fallback\"", json);
    }
```

- [ ] **Step 2: Run the new Backend tests**

Run from `C:\some-folder\Eling`:
```bash
dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~MemoryRecallDtos"
```

Expected: 7/7 pass (4 existing + 3 new).

- [ ] **Step 3: Run the full Backend test suite to verify zero regression**

```bash
dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test
```

Expected: 127/127 pass (124 existing + 3 new).

- [ ] **Step 4: Commit**

```bash
git add tests/Eling.Backend.Tests/MemoryRecallDtosTests.cs
git commit -m "test(memory): add queryMode DTO propagation and JSON tests"
```

---

### Task 9: Update spec doc to mark implementation complete and publish global

**Files:**
- Modify: `docs/superpowers/specs/2026-09-07-adaptive-query-and-or-fallback-design.md`
- (No code change — this is a doc + publishing task)

- [ ] **Step 1: Update the spec status header**

In `docs/superpowers/specs/2026-09-07-adaptive-query-and-or-fallback-design.md`, find:

```markdown
**Date:** 2026-09-07
**Status:** Draft — pending user approval
**Author:** Eling Agent
```

Replace with:

```markdown
**Date:** 2026-09-07
**Status:** Implemented (Tasks 1–8 complete, 73 Core + 127 Backend tests passing)
**Author:** Eling Agent
```

- [ ] **Step 2: Commit the spec status update**

```bash
git add docs/superpowers/specs/2026-09-07-adaptive-query-and-or-fallback-design.md
git commit -m "docs(memory): mark adaptive AND->OR fallback spec as implemented"
```

- [ ] **Step 3: Publish global so the dev binary at `~/.local/bin/eling-backend` picks up the new behavior**

```bash
powershell -File C:\some-folder\Eling\scripts\publish-global.ps1 -SkipSmokeTest
```

Expected: completes with "== Installed (smoke test skipped) ==".

- [ ] **Step 4: Verify publish succeeded**

```bash
Test-Path "$env:USERPROFILE\.local\bin\eling-backend.exe"
```

Expected: `True`.

- [ ] **Step 5: Done — report back to the user**

Report:
- All 8 code tasks committed.
- 73/73 Core tests pass, 127/127 Backend tests pass.
- Global binary at `~/.local/bin\eling-backend.exe` is updated.
- The next time the agent calls `mcp_eling_dev_memory_recall` or any recall tool, the response will include `queryMode: "and"` or `"or-fallback"` per hit, and the index will automatically expand to OR when AND yields < 3 results.
