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
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ScopedMemory>();

        // Project memories come first (project priority)
        foreach (var m in projectMemories)
        {
            var key = $"{MemoryScopeKind.Project}:{m.Id.Value}:{projectRoot}";
            if (seenKeys.Add(key))
            {
                result.Add(new ScopedMemory(m, MemoryScopeKind.Project, projectRoot));
            }
        }

        // Global memories come second
        foreach (var m in globalMemories)
        {
            var key = $"{MemoryScopeKind.Global}:{m.Id.Value}";
            if (seenKeys.Add(key))
            {
                result.Add(new ScopedMemory(m, MemoryScopeKind.Global, null));
            }
        }

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
