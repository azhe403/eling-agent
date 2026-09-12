namespace Eling.Core.Memory;

public sealed record ScopedSearchResult(
    MemoryId Id,
    double Rank,
    MemoryScopeKind Scope,
    string? ProjectRoot = null,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null);
