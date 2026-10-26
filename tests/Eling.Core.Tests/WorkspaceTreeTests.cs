using Eling.Core.Workspaces;

namespace Eling.Core.Tests;

public sealed class WorkspaceTreeTests
{
    [Fact]
    public void Build_FlatMode_SortsByLabelAndKeepsEveryRow()
    {
        var rows = new[]
        {
            new WorkspaceTreeRow("/work/zeta", false, "/work/zeta"),
            new WorkspaceTreeRow("/work/alpha", false, "/work/alpha"),
        };

        var nodes = WorkspaceTree.Build(rows, WorkspaceGroupMode.Flat);

        Assert.Equal(["alpha", "zeta"], nodes.Select(n => n.Label));
        Assert.All(nodes, n => Assert.False(n.IsRepository));
        Assert.All(nodes, n => Assert.Single(n.Roots));
    }

    [Fact]
    public void Build_RepoMode_GroupsWorktreesUnderTheirMainCheckout()
    {
        var main = new WorkspaceTreeRow("/work/repo", true, "/work/repo");
        var worktree = new WorkspaceTreeRow("/work/feature", false, "/work/repo");
        var other = new WorkspaceTreeRow("/work/other", false, "/work/other");

        var nodes = WorkspaceTree.Build([other, worktree, main], WorkspaceGroupMode.Repo);

        Assert.Equal(["other", "repo"], nodes.Select(n => n.Label));
        var repo = nodes[1];
        Assert.True(repo.IsRepository);
        Assert.Equal("/work/repo", repo.Root);
        Assert.Equal(2, repo.Roots.Count);
        Assert.True(repo.Alive);
        var child = Assert.Single(repo.Children);
        Assert.True(child.IsWorktree);
        Assert.Equal("/work/feature", child.Root);
    }

    [Fact]
    public void Build_RepoMode_WithoutRegisteredMainCheckout_UsesRepositoryRootAsPath()
    {
        var worktree = new WorkspaceTreeRow("/work/feature", false, "/work/repo");

        var nodes = WorkspaceTree.Build([worktree], WorkspaceGroupMode.Repo);

        var repo = Assert.Single(nodes);
        Assert.True(repo.IsRepository);
        Assert.Null(repo.Root);
        Assert.Equal("repo", repo.Label);
        Assert.Equal("/work/repo", repo.Path);
    }

    [Fact]
    public void Build_RepoMode_AggregatesAliveness()
    {
        var main = new WorkspaceTreeRow("/work/repo", false, "/work/repo");
        var worktree = new WorkspaceTreeRow("/work/feature", true, "/work/repo");

        var nodes = WorkspaceTree.Build([main, worktree], WorkspaceGroupMode.Repo);

        Assert.True(Assert.Single(nodes).Alive);
    }

    [Fact]
    public void Build_FolderMode_NestsARepositoryInsideAnotherRepository()
    {
        var inner = "/work/az/beta";
        var main = new WorkspaceTreeRow(inner, false, inner);
        var worktree = new WorkspaceTreeRow("/work/az/beta/beta-order", false, inner);
        var sibling = new WorkspaceTreeRow("/work/az/alpha", false, "/work/az/alpha");

        var nodes = WorkspaceTree.Build([main, worktree, sibling], WorkspaceGroupMode.Folder);

        Assert.Equal(["alpha", "beta"], nodes.Select(n => n.Label));
        Assert.Equal("beta-order", Assert.Single(nodes[1].Children).Label);
    }

    [Fact]
    public void Build_FolderMode_NestsAWorkspaceUnderTheWorkspaceThatContainsIt()
    {
        var outer = new WorkspaceTreeRow("/work/az", false, "/work/az");
        var inner = new WorkspaceTreeRow("/work/az/beta", false, "/work/az/beta");

        var nodes = WorkspaceTree.Build([outer, inner], WorkspaceGroupMode.Folder);

        var container = Assert.Single(nodes);
        Assert.Equal("az", container.Label);
        Assert.Equal("beta", Assert.Single(container.Children).Label);
    }

    [Fact]
    public void Build_FolderMode_MergesWorktreesAndNestedRepositoriesSortedByLabel()
    {
        var outer = new WorkspaceTreeRow("/work/az", false, "/work/az");
        var innerRepo = new WorkspaceTreeRow("/work/az/inner", false, "/work/az/inner");
        var worktree = new WorkspaceTreeRow("/work/az/zz-worktree", false, "/work/az");

        var nodes = WorkspaceTree.Build([outer, innerRepo, worktree], WorkspaceGroupMode.Folder);

        var container = Assert.Single(nodes);
        Assert.Equal(["inner", "zz-worktree"], container.Children.Select(c => c.Label));
    }

    [Fact]
    public void Build_EmptyInput_ReturnsNoNodes()
    {
        var nodes = WorkspaceTree.Build([], WorkspaceGroupMode.Repo);
        Assert.Empty(nodes);
    }

    [Fact]
    public void Build_PathComparison_IgnoresSeparatorsAndCase()
    {
        var a = new WorkspaceTreeRow(@"C:\Repo", false, @"C:\Repo");
        var b = new WorkspaceTreeRow("c:/repo/feature", false, "c:/repo");

        var nodes = WorkspaceTree.Build([a, b], WorkspaceGroupMode.Repo);

        var repo = Assert.Single(nodes);
        Assert.Equal(2, repo.Roots.Count);
    }
}
