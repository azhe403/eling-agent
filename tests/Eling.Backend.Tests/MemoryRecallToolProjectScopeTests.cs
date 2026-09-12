using Eling.Backend.Mcp.Tools;
using Eling.Backend.Scope;
using Eling.Core.MemoryRecall;
using Eling.Core.Scope;

namespace Eling.Backend.Tests;

public sealed class MemoryRecallToolProjectScopeTests : IDisposable
{
    private readonly string _root;
    private DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    public MemoryRecallToolProjectScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-recall-scope-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private JsonProjectScopePolicyStore NewStore()
        => new(new UserScope(_root), () => _now, Path.Combine(_root, "home"));

    private static MemoryRecallTool NewTool(string cwd, IProjectScopePolicyStore? store = null, string? home = null)
        => new(new EmptyRecallService(), cwd: cwd, userHomeDirectory: home, policyStore: store);

    [Fact]
    public async Task Recall_UninitializedProject_AdoptableTrue()
    {
        var cwd = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(cwd);

        var response = await NewTool(cwd).RecallAsync();

        Assert.Equal("uninitialized", response.ProjectScope.Posture);
        Assert.Equal("ask", response.ProjectScope.Policy);
        Assert.True(response.ProjectScope.Adoptable);
    }

    [Fact]
    public async Task Recall_DisabledPolicy_NotAdoptable()
    {
        var cwd = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(cwd);
        var store = NewStore();
        await store.SetProjectAsync(cwd, ProjectScopeDecision.Disabled);

        var response = await NewTool(cwd, store).RecallAsync();

        Assert.Equal("disabled", response.ProjectScope.Policy);
        Assert.False(response.ProjectScope.Adoptable);
    }

    [Fact]
    public async Task Recall_OwnScope_NotAdoptable()
    {
        var cwd = Path.Combine(_root, "own");
        Directory.CreateDirectory(Path.Combine(cwd, ".eling"));

        var response = await NewTool(cwd).RecallAsync();

        Assert.Equal("own-scope", response.ProjectScope.Posture);
        Assert.False(response.ProjectScope.Adoptable);
    }

    [Fact]
    public async Task Recall_UserHome_NotAdoptable()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);

        var response = await NewTool(home, home: home).RecallAsync();

        Assert.Equal("user-home", response.ProjectScope.Posture);
        Assert.False(response.ProjectScope.Adoptable);
    }

    private sealed class EmptyRecallService : IMemoryRecallService
    {
        public Task<MemoryRecallResult> RecallAsync(
            MemoryRecallContext? context,
            int recallLimit = 10,
            int recentLimit = 10,
            string? scope = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryRecallResult([], [], [], new MemoryRecallStats(0, 0, 0, 0, 0)));
    }
}
