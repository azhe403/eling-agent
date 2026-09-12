namespace Eling.Core.FileSystem;

public sealed record DeleteResult(
    string ResolvedPath,
    bool Deleted);
