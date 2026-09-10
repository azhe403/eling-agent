using Eling.Core;

namespace Eling.Core;

public sealed class MemoryMerger : IMemoryMerger
{
    public IReadOnlyCollection<ScopedMemory> MergeLists(
        IReadOnlyCollection<Memory> projectMemories,
        IReadOnlyCollection<Memory> globalMemories,
        string? projectRoot)
        => MergeLists(
            [new MemoryLevel(projectRoot ?? "", projectMemories)],
            globalMemories);

    public IReadOnlyCollection<ScopedSearchResult> MergeSearchResults(
        IReadOnlyCollection<MemorySearchResult> projectResults,
        IReadOnlyCollection<MemorySearchResult> globalResults,
        string? projectRoot)
        => MergeSearchResults(
            [new SearchResultLevel(projectRoot ?? "", projectResults)],
            globalResults);

    public IReadOnlyCollection<ScopedMemory> MergeLists(
        IReadOnlyList<MemoryLevel> levels,
        IReadOnlyCollection<Memory> globalMemories)
    {
        var result = new List<ScopedMemory>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in levels)
        {
            foreach (var m in level.Memories)
            {
                if (seen.Add(m.Id.Value))
                {
                    result.Add(new ScopedMemory(m, MemoryScopeKind.Project, level.ProjectRoot));
                }
            }
        }

        foreach (var m in globalMemories)
        {
            if (seen.Add(m.Id.Value))
            {
                result.Add(new ScopedMemory(m, MemoryScopeKind.Global, null));
            }
        }

        return result.AsReadOnly();
    }

    public IReadOnlyCollection<ScopedSearchResult> MergeSearchResults(
        IReadOnlyList<SearchResultLevel> levels,
        IReadOnlyCollection<MemorySearchResult> globalResults)
    {
        // Level-grouped contract: each scope-chain block precedes the next
        // (nearest first) and global is always last. No cross-level re-sort —
        // results arrive rank-ascending from FTS5 within a level. Nearest-wins
        // dedup by ULID keeps the higher-priority (nearer) copy.
        var result = new List<ScopedSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in levels)
        {
            foreach (var r in level.Results)
            {
                if (seen.Add(r.Id.Value))
                {
                    result.Add(ToScoped(r, MemoryScopeKind.Project, level.ProjectRoot));
                }
            }
        }

        foreach (var r in globalResults)
        {
            if (seen.Add(r.Id.Value))
            {
                result.Add(ToScoped(r, MemoryScopeKind.Global, null));
            }
        }

        return result.AsReadOnly();
    }

    private static ScopedSearchResult ToScoped(MemorySearchResult r, MemoryScopeKind scope, string? projectRoot)
        => new(r.Id, r.Rank, scope, projectRoot, r.MatchedVia, r.PorterScore, r.TrigramScore, r.QueryMode);
}

