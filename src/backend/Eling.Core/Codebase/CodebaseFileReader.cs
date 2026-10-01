using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Core.Codebase;

/// <summary>
/// Reads one workspace-relative file straight from disk, for callers that need
/// the current bytes rather than the indexed snapshot.
/// </summary>
/// <remarks>
/// Separate from <see cref="CodebaseIndexService"/> on purpose: the index serves
/// chunks out of SQLite and never touches the filesystem on the read path, while
/// this is the one place a caller-supplied path becomes a real file read. Keeping
/// it isolated means the containment guard below is testable on its own.
/// </remarks>
public sealed class CodebaseFileReader
{
    /// <summary>
    /// Largest file returned to a caller. Deliberately above the indexer's own
    /// 512 KB cap: the files worth reading from disk are exactly the ones the
    /// indexer skipped for being too large or binary, so matching its cap would
    /// hide them again.
    /// </summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    private const int BinarySniffBytes = 4096;

    private readonly ILogger<CodebaseFileReader> _logger;

    public CodebaseFileReader(ILogger<CodebaseFileReader>? logger = null)
    {
        _logger = logger ?? NullLogger<CodebaseFileReader>.Instance;
    }

    /// <summary>
    /// Reads <paramref name="relativePath"/> under <paramref name="projectRoot"/>.
    ///
    /// <paramref name="relativePath"/> is resolved against the root and then
    /// re-checked for containment, so <c>..</c> segments and a fully qualified
    /// path both resolve to a location outside the root and are refused with
    /// <see cref="ArgumentException"/>. Resolving alone is not a check —
    /// <c>GetFullPath</c> happily collapses <c>a/../../secrets</c> — so the
    /// prefix test runs on the resolved path.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The path resolves outside <paramref name="projectRoot"/>.
    /// </exception>
    public async Task<CodebaseFileRead> ReadAsync(
        string projectRoot,
        string relativePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var root = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var absolute = Path.GetFullPath(Path.Combine(root, Normalize(relativePath)));

        if (!IsUnder(root, absolute))
        {
            throw new ArgumentException(
                $"File path must stay inside the project root: '{relativePath}'.",
                nameof(relativePath));
        }

        return await ReadAbsoluteAsync(absolute, ct);
    }

    /// <summary>
    /// The read once the path is proven to be inside the root. Split out so the
    /// guard is a precondition of this method rather than something repeated in
    /// it — every branch below assumes containment already holds.
    /// </summary>
    private async Task<CodebaseFileRead> ReadAbsoluteAsync(string absolute, CancellationToken ct)
    {
        var info = new FileInfo(absolute);
        if (!info.Exists) return new CodebaseFileRead(CodebaseFileReadStatus.NotFound, null, 0);

        // The indexer reads the first 4 KB to sniff for a NUL byte; the same
        // window is enough here, and a file whose head is text but whose tail is
        // not is a case the indexer has the same blind spot in.
        if (info.Length > MaxBytes) return new CodebaseFileRead(CodebaseFileReadStatus.TooLarge, null, info.Length);
        if (await IsBinaryAsync(absolute, ct)) return new CodebaseFileRead(CodebaseFileReadStatus.Binary, null, info.Length);

        try
        {
            var content = await File.ReadAllTextAsync(absolute, ct);
            return new CodebaseFileRead(CodebaseFileReadStatus.Ok, content, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file, a denied ACL, a device the path names. The caller
            // gets a status to render, not a stack trace.
            _logger.LogWarning(ex, "Could not read {Path} for the codebase file viewer.", absolute);
            return new CodebaseFileRead(CodebaseFileReadStatus.Unreadable, null, info.Length);
        }
    }

    /// <summary>
    /// True for paths with a NUL byte in the leading window. Matches the
    /// indexer's own binary test so a file the dashboard will render as text is
    /// the same one the indexer treated as text.
    /// </summary>
    private static async Task<bool> IsBinaryAsync(string absolute, CancellationToken ct)
    {
        await using var fs = new FileStream(
            absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: BinarySniffBytes, useAsync: true);
        var buffer = new byte[BinarySniffBytes];
        var read = await fs.ReadAsync(buffer, ct);
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Index paths are stored with forward slashes; convert so the combine
    /// behaves the same on Windows. A leading separator is stripped rather than
    /// allowed to make <c>Path.Combine</c> drop the root — otherwise an
    /// "absolute" <paramref name="relativePath"/> would escape the combine and
    /// the containment check would be the only thing standing between a caller
    /// and the whole filesystem.
    /// </summary>
    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/').TrimStart('/');

    private static bool IsUnder(string root, string absolute)
        => absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
