namespace Eling.Core;

public sealed record ScopedMemory(
    Memory Memory,
    MemoryScopeKind Scope,
    string? ProjectRoot = null)
{
    public MemoryId Id => Memory.Id;
}

public sealed record ScopedSearchResult(
    MemoryId Id,
    double Rank,
    MemoryScopeKind Scope,
    string? ProjectRoot = null,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null);

/// <summary>One scope-chain level's memories for level-grouped merging.</summary>
public sealed record MemoryLevel(string ProjectRoot, IReadOnlyCollection<Memory> Memories);

/// <summary>One scope-chain level's search results for level-grouped merging.</summary>
public sealed record SearchResultLevel(string ProjectRoot, IReadOnlyCollection<MemorySearchResult> Results);
