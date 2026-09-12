namespace Eling.Core.FileSystem;

public sealed record MoveResult(
    string SourcePath,
    string ResolvedPath,
    bool Overwrote);
