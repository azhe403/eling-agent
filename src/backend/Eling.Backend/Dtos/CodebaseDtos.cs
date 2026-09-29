namespace Eling.Backend.Dtos;

/// <summary>
/// Result of <c>codebase_index</c>. Property names stay camelCase via the MCP serializer.
/// </summary>
public sealed record CodebaseIndexResponse(
    int IndexedFiles,
    int Chunks,
    int Skipped,
    int Deleted,
    long TookMs,
    bool Fresh,
    string DbPath,
    string? Status);

public sealed record CodebaseSearchHit(
    string ProjectRoot,
    string Path,
    int StartLine,
    int EndLine,
    string Content,
    double Score);

public sealed record CodebaseSearchStats(
    int TotalHits,
    long TookMs,
    bool Fresh,
    string? Hint);

public sealed record CodebaseSearchResponse(
    IReadOnlyList<CodebaseSearchHit> Hits,
    CodebaseSearchStats Stats);

public sealed record CodebaseStatusResponse(
    string ProjectRoot,
    string? LastIndexedAt,
    int FileCount,
    int ChunkCount,
    bool WatcherActive,
    int PendingFiles,
    string DbPath);
