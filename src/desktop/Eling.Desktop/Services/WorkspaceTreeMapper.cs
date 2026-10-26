using System;
using System.Collections.Generic;
using System.Linq;
using Eling.Desktop.Models;

namespace Eling.Desktop.Services;

/// <summary>
/// Maps the coordinator's <see cref="WorkspaceTreeNodeDto"/> tree into desktop view nodes
/// (<see cref="MemoryWorkspaceNode"/> for the memory sidebar, and <see cref="WorkspaceNode"/>
/// for the Folders page).
/// </summary>
public static class WorkspaceTreeMapper
{
    public static List<MemoryWorkspaceNode> ToMemoryNodes(
        IReadOnlyList<WorkspaceTreeNodeDto> nodes,
        Func<string, WorkspaceItem?> lookup)
    {
        return nodes.Select(node => ToMemoryNode(node, lookup)).ToList();
    }

    private static MemoryWorkspaceNode ToMemoryNode(
        WorkspaceTreeNodeDto dto,
        Func<string, WorkspaceItem?> lookup)
    {
        var row = dto.Root is not null ? lookup(dto.Root) : null;
        var group = new List<WorkspaceItem>();
        foreach (var r in dto.Roots)
        {
            var item = lookup(r);
            if (item is not null) group.Add(item);
        }

        var children = dto.Children.Select(c => ToMemoryNode(c, lookup)).ToList();
        var isExpanded = !dto.IsWorktree && dto.Roots.Count > 1;

        return new MemoryWorkspaceNode(
            dto.Id,
            dto.Label,
            dto.Path,
            row,
            group,
            dto.IsWorktree,
            isExpanded,
            children);
    }

    public static List<WorkspaceNode> ToFolderNodes(
        IReadOnlyList<WorkspaceTreeNodeDto> nodes,
        Func<string, WorkspaceItemDto?> lookup)
    {
        return nodes.Select(node => ToFolderNode(node, lookup)).ToList();
    }

    private static WorkspaceNode ToFolderNode(
        WorkspaceTreeNodeDto dto,
        Func<string, WorkspaceItemDto?> lookup)
    {
        var row = dto.Root is not null ? lookup(dto.Root) : null;
        var group = new List<WorkspaceItemDto>();
        foreach (var r in dto.Roots)
        {
            var item = lookup(r);
            if (item is not null) group.Add(item);
        }

        var node = new WorkspaceNode(
            dto.Id,
            dto.Label,
            dto.Path,
            row,
            group,
            dto.IsRepository,
            dto.IsWorktree);

        foreach (var childDto in dto.Children)
        {
            node.Children.Add(ToFolderNode(childDto, lookup));
        }

        return node;
    }
}
