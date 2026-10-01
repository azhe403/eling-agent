using Eling.Core.Codebase;
using Eling.Core.Scope;

namespace Eling.Backend.Tests;

/// <summary>
/// The viewer's "chunks" tab is fed by this lookup, so two things have to hold:
/// the chunks come back in line order, and a federated read answers with one
/// file rather than merging same-named files from several projects.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseFileDetailTests : IDisposable
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    private readonly string _workspace;
    private readonly string _dataDir;
    private readonly string? _originalDataDir;
    private readonly CodebaseIndexService _local;

    public CodebaseFileDetailTests()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-filedetail-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-filedetail-data-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        _local = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
    }

    [Fact]
    public async Task GetFileDetailAsync_ReturnsChunksInLineOrder()
    {
        // Long enough to be chunked, so the ordering assertion has something to
        // sort. Chunk N starts at a higher line than chunk N-1, and a query
        // that omitted ORDER BY would return them in rowid order by luck.
        var lines = string.Join('\n', Enumerable.Range(1, 1200).Select(n => $"line {n}"));
        File.WriteAllText(Path.Combine(_workspace, "big.cs"), lines);
        await _local.IndexAsync(full: true);

        var detail = await _local.GetFileDetailAsync("big.cs");

        Assert.NotNull(detail);
        Assert.True(detail!.Chunks.Count > 1, "the fixture must span several chunks for this test to mean anything");
        var starts = detail.Chunks.Select(c => c.StartLine).ToList();
        Assert.Equal(starts.OrderBy(n => n), starts);
        Assert.Equal(1, detail.Chunks[0].StartLine);
    }

    [Fact]
    public async Task GetFileDetailAsync_WithUnindexedPath_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_workspace, "a.cs"), "x\n");
        await _local.IndexAsync(full: true);

        var detail = await _local.GetFileDetailAsync("never-indexed.cs");

        Assert.Null(detail);
    }

    [Fact]
    public async Task GetFileDetailAsync_WithBlankPath_ReturnsNull()
    {
        // A blank path is a caller mistake the endpoint turns into a 400 before
        // it gets here; at this level it is simply "no such file".
        var detail = await _local.GetFileDetailAsync("   ");

        Assert.Null(detail);
    }

    [Fact]
    public async Task GetFileDetailAsync_FindsFileInSiblingRoot()
    {
        // The file exists only in the sibling, so a lookup that ignored the
        // root list would answer "not found" even though the federated scope
        // the result list was drawn from contains it.
        var sibling = Path.Combine(Path.GetTempPath(), "eling-filedetail-sib-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "only-here.cs"), "sibling content\n");
            var siblingSvc = new CodebaseIndexService(
                sibling,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(sibling)));
            await siblingSvc.IndexAsync(full: true);
            await _local.IndexAsync(full: true);

            var detail = await _local.GetFileDetailAsync("only-here.cs", [sibling]);

            Assert.NotNull(detail);
            Assert.Equal(Path.GetFullPath(sibling), detail!.ProjectRoot);
            Assert.Contains("sibling content", detail.Chunks[0].Content, StringComparison.Ordinal);
        }
        finally
        {
            CodebaseRebuildScopeTests.TryDelete(sibling);
        }
    }

    [Fact]
    public async Task GetFileDetailAsync_WithSamePathInTwoRoots_AnswersTheFirstRoot()
    {
        // Two projects can both hold "Program.cs". Returning both would leave
        // the viewer with no single document to show, so root order decides —
        // and "all" scope puts the local project first.
        var sibling = Path.Combine(Path.GetTempPath(), "eling-filedetail-dup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(_workspace, "Program.cs"), "local body\n");
            File.WriteAllText(Path.Combine(sibling, "Program.cs"), "sibling body\n");
            var siblingSvc = new CodebaseIndexService(
                sibling,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(sibling)));
            await siblingSvc.IndexAsync(full: true);
            await _local.IndexAsync(full: true);

            var local = await _local.GetFileDetailAsync("Program.cs", [_workspace, sibling]);
            var fromSibling = await _local.GetFileDetailAsync("Program.cs", [sibling, _workspace]);

            Assert.NotNull(local);
            Assert.Contains("local body", local!.Chunks[0].Content, StringComparison.Ordinal);
            Assert.NotNull(fromSibling);
            Assert.Contains("sibling body", fromSibling!.Chunks[0].Content, StringComparison.Ordinal);
        }
        finally
        {
            CodebaseRebuildScopeTests.TryDelete(sibling);
        }
    }

    [Fact]
    public async Task GetFileDetailAsync_WithSiblingRootWithoutIndex_SkipsIt()
    {
        // A root that was never indexed must not fail the lookup — the other
        // roots still have to answer.
        var unindexed = Path.Combine(Path.GetTempPath(), "eling-filedetail-none-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(unindexed);
        try
        {
            File.WriteAllText(Path.Combine(_workspace, "a.cs"), "x\n");
            await _local.IndexAsync(full: true);

            var detail = await _local.GetFileDetailAsync("a.cs", [unindexed, _workspace]);

            Assert.NotNull(detail);
            Assert.Equal(Path.GetFullPath(_workspace), detail!.ProjectRoot);
        }
        finally
        {
            CodebaseRebuildScopeTests.TryDelete(unindexed);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        CodebaseRebuildScopeTests.TryDelete(_workspace);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
    }
}
