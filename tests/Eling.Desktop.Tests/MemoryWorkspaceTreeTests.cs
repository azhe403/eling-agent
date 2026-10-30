using System.Collections.Generic;
using System.Linq;
using Eling.Desktop.Models;
using Eling.Desktop.Services;
using Xunit;

namespace Eling.Desktop.Tests;

public class MemoryWorkspaceTreeTests
{
    private static WorkspaceItem Workspace(string root, string? repo = null, bool alive = false) =>
        new(root, root, repo) { IsAlive = alive };

    private static MemoryWorkspaceNode Leaf(WorkspaceItem item, bool isWorktree = false) =>
        new($"ws:{item.Root}", item.Name, item.Root, item, [item], isWorktree, false, []);

    private static MemoryWorkspaceNode RepoNode(
        string path,
        WorkspaceItem primary,
        IReadOnlyList<WorkspaceItem> members,
        IReadOnlyList<MemoryWorkspaceNode> children,
        bool isExpanded = true) =>
        new($"repo:{path}", primary.Name, path, primary, members, false, isExpanded, children);

    [Fact]
    public void FlattenVisible_CollapsedRepository_HidesOnlyItsChildren()
    {
        var main = Workspace("repo", "repo");
        var worktree = Workspace("feature", "repo");
        var childNode = Leaf(worktree, isWorktree: true);
        var repo = RepoNode("repo", main, [main, worktree], [childNode], isExpanded: true);

        Assert.Equal(2, MemoryWorkspaceTree.FlattenVisible([repo]).Count);

        repo.IsExpanded = false;
        var visible = MemoryWorkspaceTree.FlattenVisible([repo]);
        var single = Assert.Single(visible);
        Assert.Same(repo, single);
        Assert.Equal(0, single.Depth);
    }

    [Fact]
    public void FlattenVisible_SetsDepthHierarchically()
    {
        var main = Workspace("repo", "repo");
        var worktree = Workspace("feature", "repo");
        var childNode = Leaf(worktree, isWorktree: true);
        var repo = RepoNode("repo", main, [main, worktree], [childNode], isExpanded: true);

        var visible = MemoryWorkspaceTree.FlattenVisible([repo]);
        Assert.Equal(0, visible[0].Depth);
        Assert.Equal(1, visible[1].Depth);
    }

    [Fact]
    public void ApplyExpansion_ExplicitChoiceWins_AndDefaultSurvivesForUntouchedNodes()
    {
        var main = Workspace("repo", "repo");
        var worktree = Workspace("feature", "repo");
        var childNode = Leaf(worktree, isWorktree: true);
        var repo = RepoNode("repo", main, [main, worktree], [childNode], isExpanded: true);

        // The user closed the repository; the child keeps its own default.
        var state = new WorkspaceExpansionState(expanded: [], collapsed: ["repo:repo"]);
        MemoryWorkspaceTree.ApplyExpansion([repo], state);

        Assert.False(repo.IsExpanded);
        Assert.False(childNode.IsExpanded);
    }

    [Fact]
    public void ApplyExpansion_ExplicitOpen_OverridesTheDefault()
    {
        var main = Workspace("repo", "repo");
        var childNode = Leaf(main, isWorktree: false);
        var repo = RepoNode("repo", main, [main], [childNode], isExpanded: false);

        var state = new WorkspaceExpansionState(expanded: ["repo:repo"], collapsed: []);
        MemoryWorkspaceTree.ApplyExpansion([repo], state);

        Assert.True(repo.IsExpanded);
    }

    [Fact]
    public void RefreshAggregates_ComputesTriStateCheckAndLiveness()
    {
        var main = Workspace("repo", "repo", alive: false);
        var worktree = Workspace("feature", "repo", alive: true);
        var childNode = Leaf(worktree, isWorktree: true);
        var node = RepoNode("repo", main, [main, worktree], [childNode], isExpanded: true);

        Assert.True(node.IsAlive);
        Assert.False(node.CheckState);

        main.IsSelected = true;
        node.RefreshAggregate();
        Assert.Null(node.CheckState);

        worktree.IsSelected = true;
        node.RefreshAggregate();
        Assert.True(node.CheckState);
    }
}
