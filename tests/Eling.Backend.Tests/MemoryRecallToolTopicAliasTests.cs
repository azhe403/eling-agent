using Eling.Backend.Agent.Services.Tools;
using Eling.Backend.Dtos;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Memory;
using Eling.Core.MemoryRecall;

namespace Eling.Backend.Tests;

/// <summary>
/// Locks the topics call-shape contract: topics may arrive nested under
/// <c>context</c> or as the top-level <c>topics</c> alias, and the alias
/// must never be silently dropped the way older builds ignored it.
/// </summary>
public sealed class MemoryRecallToolTopicAliasTests : IDisposable
{
    private readonly string _root;

    public MemoryRecallToolTopicAliasTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-recall-alias-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task RecallAsync_TopLevelTopicsAlias_ReachesServiceContext()
    {
        var service = new CapturingRecallService();
        var tool = new MemoryRecallTool(service, cwd: _root);

        await tool.RecallAsync(topics: ["changelog", "startup"]);

        Assert.NotNull(service.CapturedContext);
        Assert.Equal(["changelog", "startup"], service.CapturedContext!.Topics);
    }

    [Fact]
    public async Task RecallAsync_NestedContextTopics_ReachesServiceContext()
    {
        var service = new CapturingRecallService();
        var tool = new MemoryRecallTool(service, cwd: _root);

        await tool.RecallAsync(new MemoryRecallContextInput { Topics = ["changelog", "startup"] });

        Assert.Equal(["changelog", "startup"], service.CapturedContext!.Topics);
    }

    [Fact]
    public async Task RecallAsync_AliasReplacesNestedTopics_WhenBothProvided()
    {
        var service = new CapturingRecallService();
        var tool = new MemoryRecallTool(service, cwd: _root);

        await tool.RecallAsync(
            new MemoryRecallContextInput { Topics = ["nested"], FilePath = "a.cs" },
            topics: ["alias"]);

        Assert.Equal(["alias"], service.CapturedContext!.Topics);
        Assert.Equal("a.cs", service.CapturedContext!.FilePath);
    }

    [Fact]
    public async Task RecallAsync_NoTopicsAtAll_LeavesContextNull()
    {
        var service = new CapturingRecallService();
        var tool = new MemoryRecallTool(service, cwd: _root);

        await tool.RecallAsync();

        Assert.Null(service.CapturedContext);
    }

    [Fact]
    public async Task AgentTool_FlatTopicsJson_ReachesServiceContext()
    {
        var service = new CapturingRecallService();
        var agentTool = new MemoryRecallAgentTool(new MemoryRecallTool(service, cwd: _root));

        await agentTool.ExecuteAsync("{\"topics\": [\"changelog\", \"startup\"]}", CancellationToken.None);

        Assert.Equal(["changelog", "startup"], service.CapturedContext!.Topics);
    }

    [Fact]
    public async Task AgentTool_NestedContextJson_ReachesServiceContext()
    {
        var service = new CapturingRecallService();
        var agentTool = new MemoryRecallAgentTool(new MemoryRecallTool(service, cwd: _root));

        await agentTool.ExecuteAsync("{\"context\": {\"topics\": [\"changelog\", \"startup\"]}}", CancellationToken.None);

        Assert.Equal(["changelog", "startup"], service.CapturedContext!.Topics);
    }

    [Fact]
    public async Task RecallAsync_StandingBaseline_SurfacesInResponse()
    {
        var hit = new MemoryRecallHit(
            new Memory(MemoryType.Preference, "always run git hygiene"),
            ["standing"], 0.0, 0.0, "standing-baseline");
        var tool = new MemoryRecallTool(new StandingRecallService(hit), cwd: _root);

        var response = await tool.RecallAsync(new MemoryRecallContextInput { Topics = ["anything"] });

        var standing = Assert.Single(response.StandingMemories);
        Assert.Equal(MemoryType.Preference, standing.Type);
        Assert.Equal("standing-baseline", standing.QueryMode);
    }

    private sealed class CapturingRecallService : IMemoryRecallService
    {
        public MemoryRecallContext? CapturedContext { get; private set; }

        public Task<MemoryRecallResult> RecallAsync(
            MemoryRecallContext? context,
            int recallLimit = 10,
            int recentLimit = 10,
            string? scope = null,
            CancellationToken cancellationToken = default)
        {
            CapturedContext = context;
            return Task.FromResult(new MemoryRecallResult([], [], [], new MemoryRecallStats(0, 0, 0, 0, 0)));
        }
    }

    private sealed class StandingRecallService(MemoryRecallHit standing) : IMemoryRecallService
    {
        public Task<MemoryRecallResult> RecallAsync(
            MemoryRecallContext? context,
            int recallLimit = 10,
            int recentLimit = 10,
            string? scope = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryRecallResult([], [], [], new MemoryRecallStats(0, 0, 0, 0, 0), [standing]));
    }
}
