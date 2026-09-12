namespace Eling.Core.FileSystem;

public sealed record FileAppendResult(
    string ResolvedPath,
    long SizeBytes,
    bool Created);
