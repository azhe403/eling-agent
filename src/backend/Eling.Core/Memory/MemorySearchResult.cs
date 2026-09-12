namespace Eling.Core.Memory;

public readonly record struct MemorySearchResult(
    MemoryId Id,
    double Rank,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null);
