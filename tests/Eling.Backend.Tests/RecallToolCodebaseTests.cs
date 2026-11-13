using Eling.Backend.Mcp.Tools;
using Eling.Core.Codebase;
using Eling.Core.Recall;

namespace Eling.Backend.Tests;

public sealed class RecallToolCodebaseTests : IDisposable
{
    private readonly string _root;

    public RecallToolCodebaseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-recall-cb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task RecallAsync_WithoutCodebaseService_LeavesCodebaseNull()
    {
        var tool = new RecallTool(new EmptyRecallService(), cwd: _root);

        var response = await tool.RecallAsync();

        Assert.Null(response.Codebase);
    }

    [Fact]
    public async Task RecallAsync_WithCodebaseService_PopulatesCodebaseStatus()
    {
        var index = new SqliteCodebaseIndex(Path.Combine(_root, "codebase.db"));
        var codebaseSvc = new CodebaseIndexService(_root, index);
        var tool = new RecallTool(new EmptyRecallService(), cwd: _root, codebaseIndex: codebaseSvc);

        var response = await tool.RecallAsync();

        Assert.NotNull(response.Codebase);
        Assert.Equal(Path.GetFullPath(_root), response.Codebase!.ProjectRoot);
        Assert.Equal(0, response.Codebase.FileCount);
        Assert.Equal(0, response.Codebase.ChunkCount);
        Assert.False(response.Codebase.WatcherActive);
    }

    private sealed class EmptyRecallService : IRecallService
    {
        public Task<RecallResult> RecallAsync(
            RecallContext? context,
            int recallLimit = 10,
            int recentLimit = 10,
            string? scope = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new RecallResult([], [], [], new RecallStats(0, 0, 0, 0, 0)));
    }
}
