namespace Eling.Core.FileSystem;

public sealed record FileReadResult(
    string ResolvedPath,
    long SizeBytes,
    string Content,
    int StartLine,
    int EndLine,
    int TotalLines,
    bool Truncated);
