# Adaptive Query — AND → OR Fallback

**Date:** 2026-09-07
**Status:** Implemented (Tasks 1–8 complete, 73 Core + 127 Backend tests passing)
**Author:** Eling Agent

---

## Problem Statement

Current FTS5 query in `SqliteMemoryIndex.BuildPorterQuery` and `BuildTrigramQuery` uses `string.Join(' ', phrases)` to combine tokens. SQLite FTS5 parses whitespace-separated tokens as **implicit `AND`**, which causes:

1. **Excessive restriction:** A recall with 3+ topics (e.g. `['git', 'commit', 'hygiene']`) only returns memories containing ALL tokens literally — typically 0–1 results even when many relevant memories exist for subsets of the topics.
2. **Wasted context budget:** Agent retries multiple times trying different topic combinations, chewing through the 3-attempt recall gate.
3. **Counter-intuitive behavior:** Users/agents naturally think of topic lists as a "bag of related concepts" (OR semantics), not as "all of these must appear literally" (AND).

A previous design covered 4 candidate strategies (Multi-Phase, OR+filter, AND→OR Fallback, Dynamic n-Gram). After review, **Option C (AND → OR Fallback)** is selected because it preserves precision as the default while self-healing to OR when AND yields too few results.

---

## Goals

- Default precision behavior preserved (AND) — most queries unchanged.
- Fallback to OR when AND returns ≤ 2 memories — graceful expansi tanpa user retry.
- Result is **differentiated** in the MCP response: agent can see which strategy produced the final hit set via a new `queryMode` field.
- Zero breaking change to existing `MemorySearchResult` contract — additive only.
- Tests cover both branches (AND-hit-enough, AND-too-few → OR fallback).

---

## Non-Goals

- No re-ranking logic beyond existing BM25-weighted layer merge.
- No new scoring weights. The same `porter 1.0 + trigram 0.4` formula applies to both query modes.
- No changes to tag normalization, Porter stemmer, or trigram tokenizer.
- No impact on the `MatchedVia` propagation through `ScopedSearchResult` → `MemoryRecallHit` → DTO.

---

## Design

### High-Level Flow

```
MemoryRecallService.RecallAsync(query)
   │
   ▼
SqliteMemoryIndex.SearchAsync(query)
   │
   ├── Try AND-query on porter layer
   │      └── If result count >= 3  → queryMode = "and", return
   │
   ├── Try AND-query on trigram layer
   │      └── combine with porter AND-results
   │      └── If combined >= 3       → queryMode = "and", return
   │
   ├── Fallback: OR-query on both layers
   │      └── combine with weights, dedupe by id
   │      └── queryMode = "or-fallback", return
   │
   ▼
MergeRankings returns existing MemorySearchResult list
```

### Threshold: 3

The AND-OR switch fires when the combined unique-id count from AND-queries is **< 3**. Threshold matches the recall Quality Gate baseline already defined in `ServerInstructions.cs`:

> "A recall is considered complete and sufficient when it retrieves >= 3 items in 'recallMemories'."

The same number avoids double-thinking: if the server gate expects ≥ 3, the index should default to AND when it can naturally hit that target, and fall back to OR only when AND can't.

### Component Changes

#### 1. `SqliteMemoryIndex.cs` — query builders

Keep existing `BuildPorterQuery` / `BuildTrigramQuery` (AND-style) and add new `BuildPorterOrQuery` / `BuildTrigramOrQuery` (OR-style):

```csharp
private static string BuildPorterAndQuery(string[] tokens)
{
    // existing AND logic, unchanged
    var phrases = tokens.Where(t => t.Length >= 2).Select(t => $"\"{t}\"");
    return string.Join(' ', phrases);
}

private static string BuildPorterOrQuery(string[] tokens)
{
    var phrases = tokens.Where(t => t.Length >= 2).Select(t => $"\"{t}\"");
    return string.Join(" OR ", phrases);
}

private static string BuildTrigramAndQuery(string[] tokens)
{
    // existing AND logic for trigram (>= 3 chars only)
    var phrases = tokens.Where(t => t.Length >= 3).Select(t => $"\"{t}\"");
    return string.Join(' ', phrases);
}

private static string BuildTrigramOrQuery(string[] tokens)
{
    var phrases = tokens.Where(t => t.Length >= 3).Select(t => $"\"{t}\"");
    return string.Join(" OR ", phrases);
}
```

#### 2. `SqliteMemoryIndex.SearchAsync` — adaptive orchestration

Refactor `SearchAsync` to:
1. Run AND-query on porter, get porter-and-results.
2. Run AND-query on trigram, get trigram-and-results.
3. Combine porter-and ∪ trigram-and into unique-id set with weighted scores.
4. If unique-id count ≥ 3 → `queryMode = "and"`, return merged result.
5. Else → run OR-queries, combine, return `queryMode = "or-fallback"`.

The result type stays `MemorySearchResult`. To carry the mode, add a new optional field:

```csharp
public readonly record struct MemorySearchResult(
    MemoryId Id,
    double Rank,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null);   // NEW: "and" | "or-fallback"
```

Backward compatible: existing call sites that destructure by position use named or default-value semantics.

#### 3. `MemoryRecallHit` — propagate `QueryMode`

Add `QueryMode` to the `MemoryRecallHit` record so the agent can see whether the recall ran AND-only or fell back to OR:

```csharp
public sealed record MemoryRecallHit(
    Memory Memory,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null);
```

In `MemoryRecallService.RecallAsync`, capture the mode from the first hit and set it on the result or pass through on every hit. The simplest path: set `QueryMode` on each hit returned.

#### 4. `MemoryRecallMemory` DTO — surface `queryMode`

Add a `queryMode` JSON field (nullable) so the agent can see in the MCP response whether the recall hit-set was AND-only or OR-fallback:

```csharp
[JsonPropertyName("queryMode")]
public string? QueryMode { get; set; }
```

Add a setter path in `From(MemoryRecallHit)`:

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

The `recallStats` (already in the response) can also surface an aggregate `queryMode` if more useful, but per-hit gives the agent more granular context.

---

## Data Flow

```
MemoryRecallService
  → SqliteMemoryIndex.SearchAsync
      1. porter-and = SearchPorterAsync(BuildPorterAndQuery)
      2. trigram-and = SearchTrigramAsync(BuildTrigramAndQuery)
      3. combine porter-and ∪ trigram-and
      4. if uniqueCount >= 3: queryMode = "and", return
      5. else:
            porter-or = SearchPorterAsync(BuildPorterOrQuery)
            trigram-or = SearchTrigramAsync(BuildTrigramOrQuery)
            combine porter-or ∪ trigram-or, queryMode = "or-fallback"
  → MemorySearchResult with QueryMode field
  → ScopedSearchResult (propagate QueryMode)
  → MemoryRecallHit (propagate QueryMode)
  → MemoryRecallMemory DTO (JSON: queryMode)
  → MCP response
```

---

## Testing Strategy

### Unit tests (Core)

1. **`AndQuery_WhenResultsAboveThreshold_ReturnsAndMode`**
   Save 3+ memories all sharing a common token. Query with that token + a non-matching one. Expect `QueryMode = "and"` and all 3 hits returned.

2. **`AndQuery_WhenResultsBelowThreshold_FallsBackToOr`**
   Save 3 memories each containing a different single token (no overlap). Query with all 3 tokens. Expect `QueryMode = "or-fallback"` and 3 hits returned.

3. **`AndQuery_ExactlyTwoResults_FallsBackToOr`**
   Save 2 memories sharing a token. Query with that token + 1 unrelated. Expect fallback (count 2 < 3).

4. **`OrQuery_CombinesAllMatchingAcrossLayers`**
   Save memories spread across porter-only and trigram-only matches. AND returns 0; OR should return all of them.

5. **`QueryMode_PropagatesThroughMergeRankings`**
   Construct scenario with explicit AND-OR boundary to verify the mode field on the `MemorySearchResult` is correct.

### Integration tests (Backend / DTOs)

6. **`MemoryRecallMemory_FromHit_PropagatesQueryMode`**
   `From(MemoryRecallHit{QueryMode="or-fallback"})` produces DTO with `queryMode="or-fallback"`.

7. **`MemoryRecallResponse_RoundTripsQueryMode`**
   Serialize and deserialize preserves the field.

---

## Rollout

1. **Add `QueryMode` field** to `MemorySearchResult`, `MemoryRecallHit`, `MemoryRecallMemory` DTO.
2. **Refactor `SqliteMemoryIndex.SearchAsync`** to AND-then-OR orchestration with threshold = 3.
3. **Propagate through layers**: `ScopedSearchResult` (no change needed if `MemorySearchResult` change is additive and we use named param propagation — verify the existing `r.MatchedVia, r.PorterScore, r.TrigramScore` pattern works with the new field).
4. **Add tests** covering AND-hit-enough, AND-too-few → OR, and DTO propagation.
5. **Build, run all 192+ existing tests** to verify zero regression.
6. **Publish global** so the dev binary in `~/.local/bin/eling-backend` picks up the new behavior.

---

## Open Questions

1. **Threshold value:** Is 3 the right cutover, or should it be configurable per call site? Default = 3 (matches the recall Quality Gate) is the simplest stance; configurability deferred.
2. **`queryMode` granularity:** Per-hit or per-response? Per-hit is more honest (different hits could theoretically come from different branches in future multi-phase); per-response is simpler. Decision: **per-hit** for now (each hit carries the mode of the branch that produced it).
3. **Backwards compat with consumers:** `MemorySearchResult` has 13 call sites. Adding a new field with default value preserves all 13. Verified: existing positional constructions `new MemorySearchResult(id, rank, layers, porter, trigram)` still work because the new field has a default.
4. **Telemetry:** Should we log `queryMode` per recall? Useful for understanding agent behavior. Yes — add a debug-level log line in `MemoryRecallService.RecallAsync` when fallback fires.

---

## Files to Modify

| File | Change |
|------|--------|
| `src/backend/Eling.Core/Memory/MemorySearchResult.cs` | Add `QueryMode` field with default `null` |
| `src/backend/Eling.Core/Memory/Storage/SqliteMemoryIndex.cs` | Add `BuildPorterOrQuery` / `BuildTrigramOrQuery`; refactor `SearchAsync` for AND-OR orchestration |
| `src/backend/Eling.Core/MemoryRecall/MemoryRecallHit.cs` | Add `QueryMode` field with default `null` |
| `src/backend/Eling.Core/Memory/ScopedMemory.cs` | Add `QueryMode` to `ScopedSearchResult` (propagation) |
| `src/backend/Eling.Core/Memory/ScopedMemoryService.cs` | Propagate `QueryMode` in `SearchAsync` |
| `src/backend/Eling.Core/Memory/MemoryMerger.cs` | Propagate `QueryMode` in `MergeSearchResults` |
| `src/backend/Eling.Backend/Dtos/MemoryRecallMemory.cs` | Add `queryMode` JSON field + `From(hit)` setter |
| `tests/Eling.Core.Tests/FtsSearchLayerTests.cs` | New tests: AND-OR boundary, threshold, mode propagation |
| `tests/Eling.Backend.Tests/MemoryRecallDtosTests.cs` | New tests: `queryMode` JSON field, round-trip |

---

## References

- SQLite FTS5 Query Syntax — `https://www.sqlite.org/fts5.html#fts5_strings`
- `ServerInstructions.cs` — recall Quality Gate baseline (≥ 3 items)
- `SqliteMemoryIndex.cs` — current `BuildPorterQuery` / `BuildTrigramQuery` (AND-implicit)
- `MemoryRecallService.cs` — recall orchestration layer
