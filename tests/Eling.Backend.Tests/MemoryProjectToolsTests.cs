using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Backend.Mcp.Tools;
using Eling.Core;

namespace Eling.Backend.Tests;

/// <summary>
/// Consent-gated project initialization: memory_project_status reports the
/// chain posture and adoptability, memory_init_project creates `.eling` only
/// after user approval, and neither tool ever auto-creates a project dir.
/// </summary>
public sealed class MemoryProjectToolsTests : IDisposable
{
    private readonly string _root;

    public MemoryProjectToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-dummy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateDir(params string[] segments)
    {
        var path = Path.Combine([_root, ..segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Status_Uninitialized_AdoptableTrue()
    {
        var cwd = CreateDir("fresh");
        var tool = new MemoryProjectStatusTool(cwd);

        var dto = await tool.GetStatusAsync();

        Assert.Equal("uninitialized", dto.Posture);
        Assert.True(dto.Adoptable);
        Assert.False(dto.Initialized);
        Assert.Empty(dto.AncestorScopes);
        Assert.Null(dto.HeadRoot);
    }

    [Fact]
    public async Task Status_Nested_AdoptableTrue_WithAncestors()
    {
        var parent = CreateDir("parent");
        Directory.CreateDirectory(Path.Combine(parent, ".eling"));
        var cwd = CreateDir("parent", "child", "payments");

        var tool = new MemoryProjectStatusTool(cwd);

        var dto = await tool.GetStatusAsync();

        Assert.Equal("ancestor-scope", dto.Posture);
        Assert.True(dto.Adoptable);
        Assert.True(dto.Initialized);
        Assert.Equal(Path.GetFullPath(parent), dto.HeadRoot);
        Assert.Equal([Path.GetFullPath(parent)], dto.AncestorScopes);
    }

    [Fact]
    public async Task Status_OwnScope_AdoptableFalse()
    {
        var cwd = CreateDir("own");
        Directory.CreateDirectory(Path.Combine(cwd, ".eling"));

        var tool = new MemoryProjectStatusTool(cwd);

        var dto = await tool.GetStatusAsync();

        Assert.Equal("own-scope", dto.Posture);
        Assert.False(dto.Adoptable);
        Assert.True(dto.HasOwnScope);
        Assert.True(dto.Initialized);
    }

    [Fact]
    public async Task Init_CreatesDotElingAndMemories()
    {
        var cwd = CreateDir("fresh");
        var tool = new MemoryInitProjectTool(cwd);

        var result = await tool.InitAsync();

        Assert.Equal("created", result.Status);
        Assert.True(Directory.Exists(Path.Combine(cwd, ".eling", "memories")));
        Assert.Equal(Path.GetFullPath(cwd), result.HeadRoot);
        Assert.Contains(Path.GetFullPath(cwd), result.Chain);
    }

    [Fact]
    public async Task Init_Idempotent()
    {
        var cwd = CreateDir("fresh");
        var tool = new MemoryInitProjectTool(cwd);

        await tool.InitAsync();
        var second = await tool.InitAsync();

        Assert.Equal("already-initialized", second.Status);
    }

    [Fact]
    public async Task Init_UserHome_Rejected()
    {
        var fakeHome = CreateDir("fake-home");
        var tool = new MemoryInitProjectTool(fakeHome, userHomeDirectory: fakeHome);

        var result = await tool.InitAsync();

        Assert.Equal("rejected-user-home", result.Status);
        Assert.False(Directory.Exists(Path.Combine(fakeHome, ".eling")));
    }

    [Fact]
    public async Task Init_WritesGitignoreWhenMissingPatterns()
    {
        var cwd = CreateDir("repo");
        InitGitRepo(cwd);
        File.WriteAllText(Path.Combine(cwd, ".gitignore"), "# existing\n");
        var tool = new MemoryInitProjectTool(cwd);

        await tool.InitAsync();

        var gitignore = File.ReadAllText(Path.Combine(cwd, ".gitignore"));
        Assert.Contains(".eling/index.db*", gitignore);
        Assert.Contains(".eling/*.db-journal", gitignore);
        Assert.Contains(".eling/*.db-wal", gitignore);
        Assert.Contains(".eling/runtime/", gitignore);
        Assert.DoesNotContain(".eling/memories/", gitignore);
    }

    [Fact]
    public async Task Init_GitignoreNoopWhenPatternsPresent()
    {
        var cwd = CreateDir("repo");
        InitGitRepo(cwd);
        var original = "# patterns\n.eling/index.db*\n.eling/*.db-journal\n.eling/*.db-wal\n.eling/runtime/\n";
        File.WriteAllText(Path.Combine(cwd, ".gitignore"), original);
        var tool = new MemoryInitProjectTool(cwd);

        await tool.InitAsync();

        Assert.Equal(original, File.ReadAllText(Path.Combine(cwd, ".gitignore")));
    }

    private static void InitGitRepo(string dir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "init")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
    }
}