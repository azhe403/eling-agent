using System;
using System.Collections.Generic;
using System.Linq;
using Eling.Desktop.Models;
using Eling.Desktop.Services;
using Xunit;

namespace Eling.Desktop.Tests;

public class WorkspaceTreeMapperTests
{
    private static WorkspaceItem MemoryRow(string root, string? repo = null, bool alive = true) =>
        new(root, root, repo);

    private static WorkspaceItemDto FolderRow(string root, string? repo = null, bool running = true) =>
        new(
            root: root,
            repositoryRoot: repo,
            name: System.IO.Path.GetFileName(root.TrimEnd('/', '\\')),
            isRunning: running,
            processCount: running ? 1 : 0,
            singlePid: running ? 1234 : null,
            transport: null,
            ownerPort: null,
            lastHeartbeat: DateTimeOffset.UtcNow,
            totalMemoryBytes: null,
            totalCpuPercent: null,
            files: 0,
            chunks: 0,
            lastIndexedAt: null,
            dbFileName: null,
            dbPath: null,
            dbSizeBytes: 0,
            processes: []);

    [Fact]
    public void ToMemoryNodes_PreservesIdentityHierarchyAndExpansion()
    {
        var main = MemoryRow("/repo/main", "/repo/main", alive: true);
        var worktree = MemoryRow("/repo/feature", "/repo/main", alive: false);
        var other = MemoryRow("/other", alive: false);

        var lookup = new Dictionary<string, WorkspaceItem>(StringComparer.OrdinalIgnoreCase)
        {
            [main.Root] = main,
            [worktree.Root] = worktree,
            [other.Root] = other,
        };

        var dtos = new List<WorkspaceTreeNodeDto>
        {
            new(
                Id: "repo:/repo/main",
                Label: "main",
                Path: "/repo/main",
                Root: "/repo/main",
                Alive: true,
                Roots: ["/repo/main", "/repo/feature"],
                IsRepository: true,
                IsWorktree: false,
                Children:
                [
                    new(
                        Id: "ws:/repo/feature",
                        Label: "feature",
                        Path: "/repo/feature",
                        Root: "/repo/feature",
                        Alive: false,
                        Roots: ["/repo/feature"],
                        IsRepository: false,
                        IsWorktree: true,
                        Children: [])
                ]),
            new(
                Id: "ws:/other",
                Label: "other",
                Path: "/other",
                Root: "/other",
                Alive: false,
                Roots: ["/other"],
                IsRepository: false,
                IsWorktree: false,
                Children: [])
        };

        var nodes = WorkspaceTreeMapper.ToMemoryNodes(dtos, r => lookup.GetValueOrDefault(r));

        Assert.Equal(2, nodes.Count);

        var repoNode = nodes[0];
        Assert.Equal("repo:/repo/main", repoNode.Id);
        Assert.Equal("main", repoNode.Label);
        Assert.Same(main, repoNode.Row);
        Assert.Equal(2, repoNode.Group.Count);
        Assert.Same(main, repoNode.Group[0]);
        Assert.Same(worktree, repoNode.Group[1]);
        Assert.True(repoNode.IsExpanded); // multi-root repo starts expanded

        var child = Assert.Single(repoNode.Children);
        Assert.Equal("ws:/repo/feature", child.Id);
        Assert.Equal("feature", child.Label);
        Assert.True(child.IsWorktree);
        Assert.Same(worktree, child.Row);
        Assert.False(child.IsExpanded);

        var otherNode = nodes[1];
        Assert.Equal("ws:/other", otherNode.Id);
        Assert.False(otherNode.IsExpanded); // single-root leaf starts collapsed
        Assert.Same(other, otherNode.Row);
    }

    [Fact]
    public void ToMemoryNodes_SkipsUnknownRootsInGroup()
    {
        var main = MemoryRow("/repo/main", "/repo/main");
        var lookup = new Dictionary<string, WorkspaceItem>(StringComparer.OrdinalIgnoreCase)
        {
            [main.Root] = main,
        };

        var dtos = new List<WorkspaceTreeNodeDto>
        {
            new(
                Id: "repo:/repo/main",
                Label: "main",
                Path: "/repo/main",
                Root: "/repo/main",
                Alive: true,
                Roots: ["/repo/main", "/unknown/ghost"],
                IsRepository: true,
                IsWorktree: false,
                Children: [])
        };

        var nodes = WorkspaceTreeMapper.ToMemoryNodes(dtos, r => lookup.GetValueOrDefault(r));
        var node = Assert.Single(nodes);
        var resolved = Assert.Single(node.Group);
        Assert.Same(main, resolved);
    }

    [Fact]
    public void ToFolderNodes_PreservesIdentityAndChildOrder()
    {
        var main = FolderRow("/repo/main", "/repo/main");
        var worktree = FolderRow("/repo/feature", "/repo/main");

        var lookup = new Dictionary<string, WorkspaceItemDto>(StringComparer.OrdinalIgnoreCase)
        {
            [main.Root] = main,
            [worktree.Root] = worktree,
        };

        var dtos = new List<WorkspaceTreeNodeDto>
        {
            new(
                Id: "repo:/repo/main",
                Label: "main",
                Path: "/repo/main",
                Root: "/repo/main",
                Alive: true,
                Roots: ["/repo/main", "/repo/feature"],
                IsRepository: true,
                IsWorktree: false,
                Children:
                [
                    new(
                        Id: "ws:/repo/feature",
                        Label: "feature",
                        Path: "/repo/feature",
                        Root: "/repo/feature",
                        Alive: true,
                        Roots: ["/repo/feature"],
                        IsRepository: false,
                        IsWorktree: true,
                        Children: [])
                ])
        };

        var nodes = WorkspaceTreeMapper.ToFolderNodes(dtos, r => lookup.GetValueOrDefault(r));
        var repoNode = Assert.Single(nodes);

        Assert.Equal("repo:/repo/main", repoNode.Id);
        Assert.True(repoNode.IsRepository);
        Assert.Same(main, repoNode.Row);
        Assert.Equal(2, repoNode.Group.Count);
        Assert.Same(main, repoNode.Group[0]);
        Assert.Same(worktree, repoNode.Group[1]);

        var child = Assert.Single(repoNode.Children);
        Assert.Equal("ws:/repo/feature", child.Id);
        Assert.True(child.IsWorktree);
        Assert.Same(worktree, child.Row);
    }
}
