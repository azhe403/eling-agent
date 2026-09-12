namespace Eling.Core.FileSystem;

public sealed record FileWriteResult(
    string ResolvedPath,
    long SizeBytes,
    bool Overwrote);
