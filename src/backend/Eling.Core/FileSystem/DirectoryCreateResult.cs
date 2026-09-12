namespace Eling.Core.FileSystem;

public sealed record DirectoryCreateResult(
    string ResolvedPath,
    bool Created);
