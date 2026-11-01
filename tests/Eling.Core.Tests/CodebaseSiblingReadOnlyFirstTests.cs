using Eling.Core.Codebase;
using Microsoft.Data.Sqlite;

namespace Eling.Core.Tests;

/// <summary>
/// A sibling workspace's index is written by ANOTHER eling process. This
/// process only ever opens it read-only (federated status/search), and only
/// the rebuild path opens it read-write.
/// <para>
/// Regression: the connection string used <c>SqliteCacheMode.Shared</c>.
/// SQLite's shared cache is a per-file, per-process group, and the FIRST
/// connection to open a file decides the group's read-only status for every
/// later connection in that group. The federated read path (read-only) runs
/// before the rebuild (read-write) for a sibling root, so the group was born
/// read-only and the rebuild's first write failed with
/// "SQLite Error 8: attempt to write a readonly database" (SQLITE_READONLY).
/// </para>
/// <para>
/// The file is created here through a private-cache connection so this test
/// reproduces the production shape: the file already exists (another process
/// wrote it) and this process's first opener is the read-only one.
/// </para>
/// </summary>
public sealed class CodebaseSiblingReadOnlyFirstTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public CodebaseSiblingReadOnlyFirstTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-sibling-ro-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "sibling.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>
    /// Creates the file the way the OWNING process would: a normal read-write
    /// connection with its own (private) cache.
    /// </summary>
    private async Task SeedSiblingIndexAsync()
    {
        // Pooling=false: the seed must not hold the file handle itself, or
        // the assertions about "no handle left behind" would be measuring
        // this test's own connection rather than the code under test.
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS codebase_files(
              path TEXT PRIMARY KEY, hash TEXT NOT NULL, mtime INTEGER NOT NULL,
              size INTEGER NOT NULL, last_indexed_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS codebase_chunks(
              id TEXT PRIMARY KEY, file_path TEXT NOT NULL, start_line INTEGER NOT NULL,
              end_line INTEGER NOT NULL, content TEXT NOT NULL, hash TEXT NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Rebuild_SucceedsAfterAFederatedReadOnlyOpenOfTheSiblingIndex()
    {
        await SeedSiblingIndexAsync();

        // 1. The federated read path: this process opens the sibling's index
        //    read-only. Under Shared cache this creates the file's cache group
        //    in a read-only state.
        using (var reader = new SqliteCodebaseIndex(_dbPath, readOnly: true))
        {
            var stats = await reader.GetStatsAsync();
            Assert.Equal(0, stats.FileCount);
        }

        // 2. The rebuild path: read-write, and the first thing it does is
        //    create/refresh the schema.
        using var writer = new SqliteCodebaseIndex(_dbPath);
        await writer.EnsureCreatedAsync();

        // 3. And a real write of indexed content.
        await writer.ReplaceChunksAsync(
            "src/Widget.cs", "h1", 1, 42,
            [new CodebaseChunk("src/Widget.cs", 1, 42, "public sealed class Widget { }")]);

        var hits = await writer.SearchPorterAsync("Widget", 10);
        Assert.Contains(hits, h => h.Path == "src/Widget.cs");
    }

    /// <summary>
    /// A federated read must not keep the sibling's index file hostage.
    /// <para>
    /// The dashboard's status read opens EVERY registered workspace's index
    /// read-only, on every poll. When those reads pooled their connection, the
    /// handle stayed open after the connection was disposed, and on Windows an
    /// open handle makes the file undeletable — so the owner of the index
    /// could never delete it, and "delete the codebase index" silently did
    /// nothing.
    /// </para>
    /// <para>
    /// The assertion is an exclusive open of the file: if anything in this
    /// process still holds it, the open fails.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FederatedRead_LeavesNoFileHandleBehind()
    {
        await SeedSiblingIndexAsync();

        using (var reader = new SqliteCodebaseIndex(_dbPath, readOnly: true))
        {
            await reader.GetStatsAsync();
        }

        // An exclusive open succeeds only when no handle is held.
        await using var exclusive = new FileStream(
            _dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanRead);
    }

    /// <summary>
    /// The same rule for the read-write path: an index that is about to be
    /// deleted must be deletable, and its own pooled handle is released for
    /// exactly that reason.
    /// </summary>
    [Fact]
    public async Task ReleasePooledHandles_LetsTheIndexFileBeDeleted()
    {
        await SeedSiblingIndexAsync();

        using (var writer = new SqliteCodebaseIndex(_dbPath))
        {
            await writer.EnsureCreatedAsync();
            await writer.GetStatsAsync();
            writer.ReleasePooledHandles();
        }

        // The point of the release: the file can be deleted now.
        File.Delete(_dbPath);
        Assert.False(File.Exists(_dbPath));
    }
}
