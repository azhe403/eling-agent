namespace Eling.Core.Codebase;

public sealed record CodebaseFileRecord(
    string Path,
    string Hash,
    long MtimeUnix,
    long Size,
    string LastIndexedAt);

public sealed record CodebaseChunk(
    string FilePath,
    int StartLine,
    int EndLine,
    string Content);

public sealed record CodebaseHit(
    string Path,
    int StartLine,
    int EndLine,
    string Content,
    double Score);

/// <summary>
/// A codebase hit with its owning project attached, so multi-project
/// ("all scope") results carry provenance.
/// </summary>
public sealed record ScopedCodebaseHit(
    string ProjectRoot,
    string Path,
    int StartLine,
    int EndLine,
    string Content,
    double Score);

/// <summary>
/// One indexed file with its chunk count — the storage-level shape.
/// The service attaches the owning project to form
/// <see cref="CodebaseFileSummary"/> for multi-project reads.
/// </summary>
public sealed record CodebaseFileEntry(
    string Path,
    int ChunkCount,
    string LastIndexedAt);

/// <summary>
/// One indexed file with its chunk count — used for the "top files" listing.
/// </summary>
public sealed record CodebaseFileSummary(
    string ProjectRoot,
    string Path,
    int ChunkCount,
    string LastIndexedAt);

/// <summary>
/// Stats for a single index DB: file/chunk counts, newest write, DB path.
/// </summary>
public sealed record CodebaseStats(
    int FileCount,
    int ChunkCount,
    string? LastIndexedAt,
    string DbPath);

/// <summary>
/// Aggregated stats over one scope (one workspace, or many under "all"):
/// summed counts, newest write, and the roots that actually contributed.
/// </summary>
public sealed record CodebaseScopedStats(
    int Files,
    int Chunks,
    string? LastIndexedAt,
    IReadOnlyList<string> Roots);

/// <summary>
/// Outcome of one index pass: what changed, what was skipped, DB path.
/// <see cref="Status"/> is null when the pass actually ran, or
/// <see cref="CodebaseIndexService.AlreadyRunningStatus"/> when another pass
/// already held the gate and this call was turned away.
/// </summary>
public sealed record CodebaseIndexResult(
    int Files,
    int Chunks,
    int Skipped,
    int Deleted,
    string DbPath,
    string? Status = null);
