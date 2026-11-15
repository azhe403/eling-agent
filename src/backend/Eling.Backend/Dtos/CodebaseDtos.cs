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

/// <summary>
/// One indexed file — the row shape the file scopes answer with. There is
/// no line range or snippet: a file row answers "which files", not "where in
/// them".
/// </summary>
public sealed record CodebaseSearchFile(
    string ProjectRoot,
    string Path,
    int ChunkCount,
    string LastIndexedAt);

public sealed record CodebaseSearchStats(
    int TotalHits,
    long TookMs,
    bool Fresh,
    string? Hint);

/// <summary>
/// <paramref name="Mode"/> says what the query was matched against:
/// <c>content</c> (the default — the FTS hit list is populated) or
/// <c>files</c> (the path/file scopes — <paramref name="Files"/> is
/// populated and <see cref="CodebaseSearchResponse.Hits"/> is empty).
/// </summary>
public sealed record CodebaseSearchResponse(
    IReadOnlyList<CodebaseSearchHit> Hits,
    CodebaseSearchStats Stats,
    string Mode = "content",
    IReadOnlyList<CodebaseSearchFile>? Files = null);

/// <summary>
/// One chunk of an indexed file, as the file viewer renders it.
/// </summary>
public sealed record CodebaseChunkDto(
    int StartLine,
    int EndLine,
    string Content);

/// <summary>
/// An indexed file with every chunk the index holds for it. The chunk ranges
/// overlap by design, so this is a list of retrievable spans, not a
/// reconstruction of the file.
/// </summary>
public sealed record CodebaseFileDetailResponse(
    string ProjectRoot,
    string Path,
    long Size,
    string LastIndexedAt,
    IReadOnlyList<CodebaseChunkDto> Chunks);

/// <summary>
/// A raw file read from disk, or why there is none. <c>Content</c> is null for
/// every status other than <c>ok</c>.
/// </summary>
public sealed record CodebaseFileContentResponse(
    string ProjectRoot,
    string Path,
    string Status,
    long Size,
    string? Content);

public sealed record CodebaseStatusResponse(
    string ProjectRoot,
    string? LastIndexedAt,
    int FileCount,
    int ChunkCount,
    bool WatcherActive,
    int PendingFiles,
    string DbPath);
