using Eling.Core.Git;

namespace Eling.Core.Tests;

public sealed class GitRepositoryResolverTests
{
    [Fact]
    public void ResolveRepositoryRoot_MainCheckout_ReturnsTheCheckoutRoot()
    {
        using var temp = new TempWorkspace();
        var repo = temp.Folder("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var result = GitRepositoryResolver.ResolveRepositoryRoot(repo);

        Assert.Equal(repo, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_SubdirectoryOfMainCheckout_ReturnsTheCheckoutRoot()
    {
        using var temp = new TempWorkspace();
        var repo = temp.Folder("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var nested = Path.Combine(repo, "src", "backend");
        Directory.CreateDirectory(nested);

        var result = GitRepositoryResolver.ResolveRepositoryRoot(nested);

        Assert.Equal(repo, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_LinkedWorktree_ReturnsTheMainRepositoryRoot()
    {
        using var temp = new TempWorkspace();
        var main = temp.Folder("main");
        var mainGit = Path.Combine(main, ".git");
        Directory.CreateDirectory(mainGit);

        var worktree = temp.Folder("worktrees", "feature");
        var worktreeGit = Path.Combine(mainGit, "worktrees", "feature");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}");

        var result = GitRepositoryResolver.ResolveRepositoryRoot(worktree);

        Assert.Equal(main, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_LinkedWorktree_WithoutCommonDir_FallsBackToTheGitDirShape()
    {
        using var temp = new TempWorkspace();
        var main = temp.Folder("main");
        var mainGit = Path.Combine(main, ".git");
        Directory.CreateDirectory(mainGit);

        var worktree = temp.Folder("worktrees", "feature");
        var worktreeGit = Path.Combine(mainGit, "worktrees", "feature");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}");

        var result = GitRepositoryResolver.ResolveRepositoryRoot(worktree);

        Assert.Equal(main, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_AllWorktreesOfOneRepository_ShareOneRoot()
    {
        using var temp = new TempWorkspace();
        var main = temp.Folder("main");
        var mainGit = Path.Combine(main, ".git");
        Directory.CreateDirectory(mainGit);

        var first = temp.Folder("worktrees", "one");
        var second = temp.Folder("worktrees", "two");
        foreach (var name in new[] { "one", "two" })
        {
            var worktreeGit = Path.Combine(mainGit, "worktrees", name);
            Directory.CreateDirectory(worktreeGit);
            File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");
        }
        File.WriteAllText(Path.Combine(first, ".git"), $"gitdir: {Path.Combine(mainGit, "worktrees", "one")}");
        File.WriteAllText(Path.Combine(second, ".git"), $"gitdir: {Path.Combine(mainGit, "worktrees", "two")}");

        var firstRoot = GitRepositoryResolver.ResolveRepositoryRoot(first);
        var secondRoot = GitRepositoryResolver.ResolveRepositoryRoot(second);

        Assert.Equal(main, firstRoot);
        Assert.Equal(firstRoot, secondRoot);
    }

    [Fact]
    public void ResolveRepositoryRoot_NotARepository_ReturnsNull()
    {
        using var temp = new TempWorkspace();
        var plain = temp.Folder("plain");

        var result = GitRepositoryResolver.ResolveRepositoryRoot(plain);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveRepositoryRoot_UnreadableWorktreePointer_ReturnsNull()
    {
        using var temp = new TempWorkspace();
        var worktree = temp.Folder("worktree");
        File.WriteAllText(Path.Combine(worktree, ".git"), "not a pointer");

        var result = GitRepositoryResolver.ResolveRepositoryRoot(worktree);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveRepositoryRoot_RelativePath_ReturnsNull()
    {
        var result = GitRepositoryResolver.ResolveRepositoryRoot("relative/path");

        Assert.Null(result);
    }

    [Fact]
    public void ResolveRepositoryRoot_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(GitRepositoryResolver.ResolveRepositoryRoot(null));
        Assert.Null(GitRepositoryResolver.ResolveRepositoryRoot("   "));
    }
}
