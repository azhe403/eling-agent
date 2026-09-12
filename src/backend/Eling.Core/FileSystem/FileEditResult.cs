namespace Eling.Core.FileSystem;

public sealed record FileEditResult(
    string ResolvedPath,
    long SizeBytes,
    int Replacements);
