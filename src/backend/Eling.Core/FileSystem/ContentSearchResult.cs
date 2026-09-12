namespace Eling.Core.FileSystem;

public sealed record ContentSearchResult(
    string ResolvedBasePath,
    string Pattern,
    int MatchCount,
    bool Truncated,
    IReadOnlyList<ContentMatch> Matches);
