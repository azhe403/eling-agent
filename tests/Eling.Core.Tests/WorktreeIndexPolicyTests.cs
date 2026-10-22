using Eling.Core.Codebase;

namespace Eling.Core.Tests;

/// <summary>
/// A linked worktree inherits the indexing state of its main repository: when
/// the repository's index is already active, the worktree builds its own index
/// without asking again. These tests pin the decision, not the indexing pass
/// itself — the pass is proved end to end by the watcher tests.
/// </summary>
[Collection(WorktreeIndexCollection.Name)]
public sealed class WorktreeIndexPolicyTests
{
    [Fact]
    public async Task EvaluateAsync_MainCheckout_DoesNotInheritTheRepositoryIndex()
    {
        using var temp = new TempWorkspace();
        var repo = temp.Folder("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var decision = await WorktreeIndexPolicy.EvaluateAsync(repo, (_ , _) => Task.FromResult(true));

        Assert.Equal(WorktreeIndexKind.NotAWorktree, decision.Kind);
        Assert.Null(decision.RepositoryRoot);
    }

    [Fact]
    public async Task EvaluateAsync_FolderOutsideARepository_DoesNotInheritTheRepositoryIndex()
    {
        using var temp = new TempWorkspace();
        var plain = temp.Folder("plain");

        var decision = await WorktreeIndexPolicy.EvaluateAsync(plain, (_ , _) => Task.FromResult(true));

        Assert.Equal(WorktreeIndexKind.NotAWorktree, decision.Kind);
        Assert.Null(decision.RepositoryRoot);
    }

    [Fact]
    public async Task EvaluateAsync_LinkedWorktreeOfAnIndexedRepository_InheritsTheRepositoryIndex()
    {
        using var temp = new TempWorkspace();
        var main = CreateMainRepository(temp);
        var worktree = CreateWorktree(temp, main, "feature");

        var decision = await WorktreeIndexPolicy.EvaluateAsync(worktree, (_ , _) => Task.FromResult(true));

        Assert.Equal(WorktreeIndexKind.Inherit, decision.Kind);
        Assert.Equal(main, decision.RepositoryRoot);
        Assert.True(decision.Inherits);
    }

    [Fact]
    public async Task EvaluateAsync_LinkedWorktreeOfAnUnindexedRepository_DoesNotInherit()
    {
        using var temp = new TempWorkspace();
        var main = CreateMainRepository(temp);
        var worktree = CreateWorktree(temp, main, "feature");

        var decision = await WorktreeIndexPolicy.EvaluateAsync(worktree, (_, _) => Task.FromResult(false));

        Assert.Equal(WorktreeIndexKind.RepositoryIndexInactive, decision.Kind);
        Assert.Equal(main, decision.RepositoryRoot);
        Assert.False(decision.Inherits);
    }

    [Fact]
    public async Task EvaluateAsync_SubdirectoryOfALinkedWorktree_InheritsTheRepositoryIndex()
    {
        using var temp = new TempWorkspace();
        var main = CreateMainRepository(temp);
        var worktree = CreateWorktree(temp, main, "feature");
        var nested = Path.Combine(worktree, "src", "backend");
        Directory.CreateDirectory(nested);

        var decision = await WorktreeIndexPolicy.EvaluateAsync(nested, (_ , _) => Task.FromResult(true));

        Assert.Equal(WorktreeIndexKind.Inherit, decision.Kind);
        Assert.Equal(main, decision.RepositoryRoot);
    }

    [Fact]
    public async Task EvaluateAsync_UnreadableWorktreePointer_DoesNotInherit()
    {
        using var temp = new TempWorkspace();
        var worktree = temp.Folder("worktree");
        File.WriteAllText(Path.Combine(worktree, ".git"), "not a pointer");

        var decision = await WorktreeIndexPolicy.EvaluateAsync(worktree, (_ , _) => Task.FromResult(true));

        Assert.Equal(WorktreeIndexKind.NotAWorktree, decision.Kind);
    }

    [Fact]
    public async Task EvaluateAsync_WorkspaceThatIsNotAWorktree_NeverProbesTheRepositoryIndex()
    {
        using var temp = new TempWorkspace();
        var repo = temp.Folder("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var probed = false;

        var decision = await WorktreeIndexPolicy.EvaluateAsync(repo, (_, _) =>
        {
            probed = true;
            return Task.FromResult(true);
        });

        Assert.Equal(WorktreeIndexKind.NotAWorktree, decision.Kind);
        Assert.False(probed);
    }

    /// <summary>
    /// A main checkout: a <c>.git</c> directory marks it.
    /// </summary>
    private static string CreateMainRepository(TempWorkspace temp)
    {
        var main = temp.Folder("main");
        Directory.CreateDirectory(Path.Combine(main, ".git"));
        return main;
    }

    /// <summary>
    /// A linked worktree of <paramref name="main"/>: a <c>.git</c> file points
    /// at <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>, and that directory's
    /// <c>commondir</c> names the main repository's <c>.git</c>.
    /// </summary>
    private static string CreateWorktree(TempWorkspace temp, string main, string name)
    {
        var worktree = temp.Folder("worktrees", name);
        var worktreeGit = Path.Combine(main, ".git", "worktrees", name);
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}");
        return worktree;
    }
}
