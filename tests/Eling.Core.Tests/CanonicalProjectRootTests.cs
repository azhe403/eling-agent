using Eling.Core.Scope;

namespace Eling.Core.Tests;

public sealed class CanonicalProjectRootTests
{
    [Fact]
    public void Resolve_WorktreesSharingOneToplevel_MapToSameCanonical()
    {
        const string topLevel = "/repo/main";
        Task<string?> Probe(string _) => Task.FromResult<string?>(topLevel);

        var a = CanonicalProjectRoot.Resolve("/repo/main-wt1", Probe);
        var b = CanonicalProjectRoot.Resolve("/repo/main/sub/dir", Probe);

        Assert.Equal(Path.GetFullPath(topLevel), a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Resolve_ProbeNull_FallsBackToStart()
    {
        var result = CanonicalProjectRoot.Resolve("/work/plain", _ => Task.FromResult<string?>(null));

        Assert.Equal(Path.GetFullPath("/work/plain"), result);
    }

    [Fact]
    public void Resolve_ProbeThrows_FallsBackToStart()
    {
        Task<string?> Probe(string _) => throw new InvalidOperationException("no git");

        var result = CanonicalProjectRoot.Resolve("/work/plain", Probe);

        Assert.Equal(Path.GetFullPath("/work/plain"), result);
    }

    [Fact]
    public void Resolve_SameRepoWorktrees_ShareOneLocalShard()
    {
        const string topLevel = "/repo/main";
        Task<string?> Probe(string _) => Task.FromResult<string?>(topLevel);

        var a = ElingPaths.ResolveProjectLocalDir(CanonicalProjectRoot.Resolve("/wt1", Probe), "/xdg/data");
        var b = ElingPaths.ResolveProjectLocalDir(CanonicalProjectRoot.Resolve("/wt2", Probe), "/xdg/data");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Resolve_RealGitRepo_MapsToToplevel()
    {
        // Repo-root discovery: the test working directory is inside this git
        // repo, so the real probe must land on its toplevel.
        var canonical = CanonicalProjectRoot.Resolve(AppContext.BaseDirectory);

        Assert.True(Directory.Exists(Path.Combine(canonical, ".git")));
    }
}
