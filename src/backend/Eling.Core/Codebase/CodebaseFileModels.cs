namespace Eling.Core.Codebase;

/// <summary>
/// One indexed file plus every chunk the index holds for it, with the owning
/// project attached. The service resolves this across project roots; the
/// first root that actually carries the file wins, so a federated read
/// answers with one file rather than several same-named ones.
/// </summary>
public sealed record ScopedCodebaseFileDetail(
    string ProjectRoot,
    string Path,
    long Size,
    string LastIndexedAt,
    IReadOnlyList<CodebaseChunk> Chunks);

/// <summary>
/// Why a raw file read could not produce content. Only <see cref="Ok"/>
/// carries text; the rest exist so the caller can say what is wrong instead
/// of showing an empty file.
/// </summary>
public enum CodebaseFileReadStatus
{
    Ok,
    NotFound,
    TooLarge,
    Binary,
    Unreadable
}

/// <summary>
/// A raw workspace-relative file read straight from disk, or the reason there
/// is no content. <see cref="Content"/> is null for every status but
/// <see cref="CodebaseFileReadStatus.Ok"/>.
/// </summary>
public sealed record CodebaseFileRead(
    CodebaseFileReadStatus Status,
    string? Content,
    long Size);
