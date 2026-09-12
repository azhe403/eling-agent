namespace Eling.Core.FileSystem;

public sealed record CopyResult(
    string SourcePath,
    string ResolvedPath,
    bool Overwrote,
    int EntriesCopied);
