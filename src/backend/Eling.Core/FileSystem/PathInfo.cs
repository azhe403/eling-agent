namespace Eling.Core.FileSystem;

public sealed record PathInfo(
    string ResolvedPath,
    bool Exists,
    PathKind Kind,
    long? SizeBytes);
