using Eling.Core.Codebase;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The stale-row sweep may only delete a file's rows once the file is proven
/// gone. Being indexed but absent from the current pass is not proof: a file
/// under an excluded directory, or one whose directory failed to enumerate, is
/// still on disk. Dropping its rows silently deletes indexed content, and the
/// only way to recover is re-indexing from scratch.
/// </summary>
public sealed class CodebaseStaleSweepTests : IDisposable
{
    private readonly string _root;

    public CodebaseStaleSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-sweep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task FullPass_KeepsRowsForFilesUnderAnExcludedDirectory()
    {
        // A scoped pass reaches into bin/ — EnumerateFiles applies only the
        // ignore rules to a scope root, not the excluded-directory list — while
        // a full pass never descends there. The row must survive the full pass.
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(_root, "bin", "gen.cs"), "class Gen { }");
        await File.WriteAllTextAsync(Path.Combine(_root, "app.cs"), "class App { }");

        var index = new SqliteCodebaseIndex(Path.Combine(_root, "index.db"));
        var service = new CodebaseIndexService(_root, index);

        var scoped = await service.IndexAsync(pathPrefixes: ["bin"]);
        Assert.Equal(1, scoped.Files);
        Assert.Contains("bin/gen.cs", await index.ListPathsAsync());

        var full = await service.IndexAsync(full: true);
        Assert.Equal(0, full.Deleted);
        Assert.Contains("bin/gen.cs", await index.ListPathsAsync());
        Assert.Contains("app.cs", await index.ListPathsAsync());
    }

    [Fact]
    public async Task FullPass_StillDropsRowsForFilesThatAreActuallyGone()
    {
        // The guard must not become a blanket "keep everything": a deleted file
        // has no row left to search, and leaving it would grow the index forever.
        var kept = Path.Combine(_root, "keep.cs");
        var removed = Path.Combine(_root, "removed.cs");
        await File.WriteAllTextAsync(kept, "class Keep { }");
        await File.WriteAllTextAsync(removed, "class Removed { }");

        var index = new SqliteCodebaseIndex(Path.Combine(_root, "index.db"));
        var service = new CodebaseIndexService(_root, index);

        await service.IndexAsync();
        Assert.Contains("removed.cs", await index.ListPathsAsync());

        File.Delete(removed);
        var full = await service.IndexAsync(full: true);

        Assert.Equal(1, full.Deleted);
        Assert.Contains("keep.cs", await index.ListPathsAsync());
        Assert.DoesNotContain("removed.cs", await index.ListPathsAsync());
    }

    public void Dispose()
    {
        // Best-effort teardown: a leftover temp dir must never fail an otherwise
        // passing run. A locked or still-in-use file is the only realistic
        // failure, and there is nothing useful to report once the test is over.
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
