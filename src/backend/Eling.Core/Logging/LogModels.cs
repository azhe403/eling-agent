namespace Eling.Core.Logging;

public sealed record FilteredTailResult(
    int Total,
    int Filtered,
    List<string> Lines);

public sealed record LogFileInfo(
    string Name,
    long SizeBytes,
    DateTime LastWriteTimeUtc);

public sealed record LogReadResult(
    string File,
    string Directory,
    bool Exists,
    int TotalLines,
    int FilteredTotal,
    int Returned,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string>? Files = null);