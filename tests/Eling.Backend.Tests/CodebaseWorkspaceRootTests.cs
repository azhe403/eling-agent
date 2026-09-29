using Eling.Backend.Bootstrap;
using Eling.Backend.Mcp;
using Eling.Core;
using Eling.Core.Codebase;
using Eling.Core.Runtime;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;

namespace Eling.Backend.Tests;

/// <summary>
/// Codebase index identity: the index is rooted at the working directory
/// (the workspace the backend was launched in), never at the nearest
/// <c>.eling</c> ancestor. Memory scope follows <c>.eling</c>; code follows
/// where the agent actually works.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseWorkspaceRootTests : IDisposable
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    private readonly string _parent;
    private readonly string _workspace;
    private readonly string _dataDir;
    private readonly string? _originalDataDir;
    private readonly ServiceProvider _provider;

    public CodebaseWorkspaceRootTests()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _parent = Path.Combine(Path.GetTempPath(), "eling-ws-parent-" + tag);
        _workspace = Path.Combine(_parent, "sub");
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-ws-data-" + tag);
        Directory.CreateDirectory(Path.Combine(_parent, ".eling"));
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        // Ancestor owns the memory scope (.eling); the subfolder is cwd.
        var chain = new ScopeChain(_workspace, [new ProjectScope(_parent)]);
        var userScope = UserScope.Resolve(Path.Combine(_dataDir, "user-scope"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElingCoreServices(chain, userScope);
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public void IndexService_IsRootedAtCwd_NotAtElingAncestor()
    {
        using var scope = _provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CodebaseIndexService>();

        Assert.Equal(Path.GetFullPath(_workspace), svc.ProjectRoot);
    }

    [Fact]
    public async Task IndexService_DbPath_IsKeyedByWorkspace()
    {
        using var scope = _provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CodebaseIndexService>();

        var stats = await svc.GetStatsAsync();

        Assert.Equal(ElingPaths.ResolveCodebaseDbPath(_workspace), stats.DbPath);
    }

    [Fact]
    public async Task IndexAsync_IndexesWorkspaceOnly()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "InScope.cs"),
            "public sealed class ZephyrWorkspaceWidget { }");
        await File.WriteAllTextAsync(
            Path.Combine(_parent, "OutOfScope.cs"),
            "public sealed class QuokkaAncestorWidget { }");

        using var scope = _provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CodebaseIndexService>();
        await svc.IndexAsync();

        var inside = await svc.SearchAsync("ZephyrWorkspaceWidget");
        Assert.Contains(inside, h => h.Path.EndsWith("InScope.cs", StringComparison.Ordinal));

        var outside = await svc.SearchAsync("QuokkaAncestorWidget");
        Assert.Empty(outside);
    }

    [Fact]
    public void RuntimeRegistration_WorkspaceIsCwd_ProjectStaysAtEling()
    {
        var chain = new ScopeChain(_workspace, [new ProjectScope(_parent)]);
        var userScope = UserScope.Resolve(Path.Combine(_dataDir, "user-scope"));
        var context = new ProjectContext(chain, userScope, Path.Combine(_parent, ".eling"), false);

        var reg = RuntimeSelfRegistration.Build(context);

        Assert.Equal(Path.GetFullPath(_workspace), reg.WorkspaceRoot);
        Assert.Equal(Path.GetFullPath(_parent), reg.ProjectRoot);
    }

    [Fact]
    public void CodebaseRoot_FallsBackToProjectRoot_WhenWorkspaceMissing()
    {
        var legacy = new RuntimeInfo { ProjectRoot = _parent, WorkspaceRoot = "" };
        Assert.Equal(_parent, legacy.CodebaseRoot());

        var current = new RuntimeInfo { ProjectRoot = _parent, WorkspaceRoot = _workspace };
        Assert.Equal(_workspace, current.CodebaseRoot());
    }

    [Theory]
    [InlineData("bin/Debug/app.dll", true)]
    [InlineData("src/bin/x.cs", true)]
    [InlineData(".git/objects/ab", true)]
    [InlineData("node_modules/pkg/f.js", true)]
    [InlineData("../outside.cs", true)]
    [InlineData("", true)]
    [InlineData("src/app.cs", false)]
    [InlineData("README.md", false)]
    public void IsSkippedPath_MatchesIndexerExclusions(string rel, bool expected)
    {
        Assert.Equal(expected, CodebaseIndexService.IsSkippedPath(rel));
    }

    [Theory]
    [InlineData("*.log\n", "trace.log", true)]
    [InlineData("*.log\n", "src/trace.log", true)]
    [InlineData("build/\n", "build/out.dll", true)]
    [InlineData("build/\n", "src/build/out.dll", true)]
    [InlineData("/root-only.txt\n", "root-only.txt", true)]
    [InlineData("/root-only.txt\n", "sub/root-only.txt", false)]
    [InlineData("*.log\n!keep.log\n", "keep.log", false)]
    [InlineData("*.log\n!keep.log\n", "drop.log", true)]
    [InlineData("# comment\n\n*.tmp\n", "a.tmp", true)]
    [InlineData("doc/**/*.md\n", "doc/a/b/c.md", true)]
    [InlineData("doc/**/*.md\n", "other/c.md", false)]
    public void GitignoreFilter_MatchesGitSemantics(string gitignore, string rel, bool expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "eling-gi-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".gitignore"), gitignore);
            var filter = GitignoreFilter.Load(dir);
            Assert.Equal(expected, filter.IsIgnored(rel));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GitignoreFilter_NestedFileOverridesRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eling-gi-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "*.txt\n");
            File.WriteAllText(Path.Combine(dir, "sub", ".gitignore"), "!keep.txt\n");
            var filter = GitignoreFilter.Load(dir);
            Assert.False(filter.IsIgnored("sub/keep.txt"));
            Assert.True(filter.IsIgnored("sub/drop.txt"));
            Assert.True(filter.IsIgnored("drop.txt"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task IndexAsync_HonorsGitignore()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "ignored"));
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".gitignore"), "ignored/\n*.log\n");
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "ignored", "Nope.cs"),
            "public sealed class QuokkaIgnoredWidget { }");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "trace.log"), "junk");
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "src", "Keep.cs"),
            "public sealed class ZephyrKeptWidget { }");

        using var scope = _provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CodebaseIndexService>();
        await svc.IndexAsync();

        var kept = await svc.SearchAsync("ZephyrKeptWidget");
        Assert.Contains(kept, h => h.Path.EndsWith("Keep.cs", StringComparison.Ordinal));
        Assert.Empty(await svc.SearchAsync("QuokkaIgnoredWidget"));
    }

    public void Dispose()
    {
        _provider.Dispose();
        Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        try { Directory.Delete(_parent, recursive: true); } catch { }
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
