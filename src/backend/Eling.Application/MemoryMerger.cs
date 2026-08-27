using Eling.Core;

namespace Eling.Application;

public sealed class MemoryMerger : IMemoryMerger
{
    private const double ProjectPriorityBoost = 1000.0;

    public IReadOnlyCollection<ScopedMemory> MergeLists(
        IReadOnlyCollection<Memory> projectMemories,
        IReadOnlyCollection<Memory> globalMemories,
        string? projectRoot)
    {
        var result = new List<ScopedMemory>();
        foreach (var m in projectMemories)
        {
            result.Add(new ScopedMemory(m, MemoryScopeKind.Project, projectRoot));
        }
        foreach (var m in globalMemories)
        {
            result.Add(new ScopedMemory(m, MemoryScopeKind.Global, null));
        }

        // Safe dedup: same scoped identity (scope+id) only
        // If exact same id appears in both scopes, they are distinct (different scope)
        return result.AsReadOnly();
    }

    public IReadOnlyCollection<ScopedSearchResult> MergeSearchResults(
        IReadOnlyCollection<MemorySearchResult> projectResults,
        IReadOnlyCollection<MemorySearchResult> globalResults,
        string? projectRoot)
    {
        var merged = new List<ScopedSearchResult>();

        foreach (var r in projectResults)
        {
            // Project gets priority boost so it ranks above comparable global results
            // Lower rank = more relevant in FTS5 bm25 (negative). We boost by subtracting.
            var boostedRank = r.Rank - ProjectPriorityBoost;
            merged.Add(new ScopedSearchResult(r.Id, boostedRank, MemoryScopeKind.Project, projectRoot));
        }

        foreach (var r in globalResults)
        {
            merged.Add(new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Global, null));
        }

        // Deduplicate: only if same scoped identity appears twice (should not happen)
        var seen = new HashSet<string>();
        var deduped = new List<ScopedSearchResult>();
        foreach (var item in merged.OrderBy(x => x.Rank))
        {
            var key = $"{item.Scope}:{item.Id.Value}";
            if (seen.Add(key))
            {
                deduped.Add(item);
            }
        }

        return deduped.AsReadOnly();
    }
}
