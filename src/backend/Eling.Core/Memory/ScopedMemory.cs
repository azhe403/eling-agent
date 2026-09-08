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
