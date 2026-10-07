using Eling.Core.Codebase;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// An index file can exist without having a schema. Opening one creates it
/// (ReadWriteCreate) before any table exists, so a pass that dies between
/// those two steps — or a reader that only ever opened the file — leaves a
/// real file that is not an index. File existence is therefore not a usable
/// stand-in for "indexed", and a read that trusts it raises "no such table"
/// instead of reporting an empty index, which is what the caller means.
/// </summary>
public sealed class CodebaseUninitializedIndexTests : IDisposable
{
    private readonly string _root;

    public CodebaseUninitializedIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-uninit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>A file that exists on disk but has no tables in it.</summary>
    private string SchemaLessDbPath()
    {
        var path = Path.Combine(_root, "index.db");
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public async Task GetStats_OnAnIndexWithNoSchema_ReportsEmptyRatherThanThrowing()
    {
        using var index = new SqliteCodebaseIndex(SchemaLessDbPath());

        var stats = await index.GetStatsAsync();

        Assert.Equal(0, stats.FileCount);
        Assert.Equal(0, stats.ChunkCount);
        Assert.Null(stats.LastIndexedAt);
    }

    [Fact]
    public async Task ListPaths_OnAnIndexWithNoSchema_IsEmptyRatherThanThrowing()
    {
        using var index = new SqliteCodebaseIndex(SchemaLessDbPath());

        Assert.Empty(await index.ListPathsAsync());
    }

    [Fact]
    public async Task ListRecentFiles_OnAnIndexWithNoSchema_IsEmptyRatherThanThrowing()
    {
        using var index = new SqliteCodebaseIndex(SchemaLessDbPath());

        Assert.Empty(await index.ListRecentFilesAsync(10));
    }

    [Fact]
    public async Task SearchPorter_OnAnIndexWithNoSchema_IsEmptyRatherThanThrowing()
    {
        using var index = new SqliteCodebaseIndex(SchemaLessDbPath());

        var hits = await index.SearchPorterAsync("alpha", 10);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchTrigram_OnAnIndexWithNoSchema_IsEmptyRatherThanThrowing()
    {
        using var index = new SqliteCodebaseIndex(SchemaLessDbPath());

        var hits = await index.SearchTrigramAsync("alpha", 10);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task GetFile_OnAnIndexWithNoSchema_ReturnsNullRatherThanThrowing()
    {
        using var index = new SqliteCodebaseIndex(SchemaLessDbPath());

        var file = await index.GetFileAsync("src/App.cs");

        Assert.Null(file);
    }

    [Fact]
    public async Task DeleteFile_OnAnIndexWithNoSchema_DoesNotThrow()
    {
        using var index = new SqliteCodebaseIndex(SchemaLessDbPath());

        var exception = await Record.ExceptionAsync(() => index.DeleteFileAsync("src/App.cs"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SearchPorter_WhenDatabaseFileDoesNotExist_ReturnsEmptyWithoutCreatingFile()
    {
        var nonExistentPath = Path.Combine(_root, "nonexistent.db");
        using var index = new SqliteCodebaseIndex(nonExistentPath);

        var hits = await index.SearchPorterAsync("alpha", 10);

        Assert.Empty(hits);
        Assert.False(File.Exists(nonExistentPath));
    }
}
