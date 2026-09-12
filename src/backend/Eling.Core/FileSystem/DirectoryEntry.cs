namespace Eling.Core.FileSystem;

public sealed record DirectoryEntry(
    string Path,
    string Name,
    PathKind Kind,
    long? SizeBytes);
