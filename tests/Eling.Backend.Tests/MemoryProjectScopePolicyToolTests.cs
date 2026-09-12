using Eling.Backend.Mcp.Tools;
using Eling.Backend.Scope;
using Eling.Core.Scope;

namespace Eling.Backend.Tests;

public sealed class MemoryProjectScopePolicyToolTests : IDisposable
{
    private readonly string _root;
    private DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    public MemoryProjectScopePolicyToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-policy-tool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private JsonProjectScopePolicyStore NewStore()
        => new(new UserScope(_root), () => _now, Path.Combine(_root, "home"));

    private MemoryProjectScopePolicyTool NewTool(string cwd, IProjectScopePolicyStore? store = null, string? home = null)
        => new(store ?? NewStore(), cwd, home ?? Path.Combine(_root, "home"));

    [Fact]
    public async Task SetProject_Disable_Ok()
    {
        var cwd = Path.Combine(_root, "acme");
        Directory.CreateDirectory(cwd);
        var store = NewStore();

        var result = await NewTool(cwd, store).SetAsync("disabled");

        Assert.True(result.Ok);
        Assert.Equal("disabled", result.Decision);
        Assert.Equal(ProjectScopePolicy.NormalizeRoot(cwd), result.AppliedTo);
        Assert.True(result.Policy!.Projects.TryGetValue(ProjectScopePolicy.NormalizeRoot(cwd), out var entry));
        Assert.Equal("disabled", entry!.Decision);
    }

    [Fact]
    public async Task SetProject_ThenClear_RemovesEntry()
    {
        var cwd = Path.Combine(_root, "acme");
        Directory.CreateDirectory(cwd);
        var tool = NewTool(cwd);

        await tool.SetAsync("disabled");
        var cleared = await tool.SetAsync("clear");

        Assert.True(cleared.Ok);
        Assert.Empty(cleared.Policy!.Projects);
    }

    [Fact]
    public async Task InvalidDecision_ReturnsInvalidArgument()
    {
        var result = await NewTool(_root).SetAsync("nope");

        Assert.False(result.Ok);
        Assert.Equal("invalid_argument", result.Code);
    }

    [Fact]
    public async Task Pattern_Target_RequiresGlob()
    {
        var result = await NewTool(_root).SetAsync("disabled", target: "pattern");

        Assert.False(result.Ok);
        Assert.Equal("invalid_argument", result.Code);
    }

    [Fact]
    public async Task UserHome_IsRejected()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);

        var result = await NewTool(home, home: home).SetAsync("disabled");

        Assert.False(result.Ok);
        Assert.Equal("rejected_user_home", result.Code);
    }

    [Fact]
    public async Task Clear_WhenMissing_ReturnsNotFound()
    {
        var result = await NewTool(_root).SetAsync("clear");

        Assert.False(result.Ok);
        Assert.Equal("not_found", result.Code);
    }

    [Fact]
    public async Task SetPattern_ThenClear_Succeeds()
    {
        var tool = NewTool(_root);

        var set = await tool.SetAsync("disabled", target: "pattern", pattern: "~/work/**");
        Assert.True(set.Ok);
        Assert.Single(set.Policy!.Patterns);

        var clear = await tool.SetAsync("clear", target: "pattern", pattern: "~/work/**");
        Assert.True(clear.Ok);
        Assert.Empty(clear.Policy!.Patterns);
    }

    [Fact]
    public async Task SetDefault_Disabled()
    {
        var result = await NewTool(_root).SetAsync("disabled", target: "default");

        Assert.True(result.Ok);
        Assert.Equal("disabled", result.Policy!.Default);
    }
}
