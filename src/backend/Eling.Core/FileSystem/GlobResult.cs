namespace Eling.Core.FileSystem;

public sealed record GlobResult(
    string ResolvedBasePath,
    string Pattern,
    int MatchCount,
    bool Truncated,
    IReadOnlyList<DirectoryEntry> Matches);
