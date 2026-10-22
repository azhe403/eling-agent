namespace Eling.Core.Codebase;

/// <summary>
/// How much disk one workspace's codebase index occupies.
/// </summary>
public static class CodebaseIndexSize
{
    /// <summary>
    /// The bytes an index occupies on disk: its database file plus the
    /// write-ahead log beside it.
    /// </summary>
    /// <remarks>
    /// The WAL counts because it holds committed writes that have not been
    /// checkpointed into the main file yet — real bytes that a directory
    /// listing counts and a length read of the database alone does not. The
    /// shared-memory sidecar (<c>-shm</c>) does not count: it is a transient
    /// index over the WAL, rebuilt on every open, and carries no data of its
    /// own.
    /// <para>
    /// Never throws. A missing, locked, or unreadable file counts as zero
    /// rather than failing the caller's read of the whole store.
    /// </para>
    /// </remarks>
    public static long Measure(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath)) return 0;
        return LengthOf(dbPath) + LengthOf(dbPath + "-wal");
    }

    private static long LengthOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }
}
