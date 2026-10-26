using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eling.Desktop.Models;

namespace Eling.Desktop.Services;

/// <summary>
/// Expansion and aggregate helpers for the memory sidebar's workspace tree.
/// (Tree building itself is owned by the coordinator and mapped via <see cref="WorkspaceTreeMapper"/>).
/// </summary>
public static class MemoryWorkspaceTree
{
    public static List<MemoryWorkspaceNode> FlattenVisible(
        IEnumerable<MemoryWorkspaceNode> roots)
    {
        return Flatten(roots, 0).ToList();
    }

    public static void CaptureExpansion(
        IEnumerable<MemoryWorkspaceNode> roots,
        IDictionary<string, bool> state)
    {
        foreach (var node in roots)
        {
            state[node.Id] = node.IsExpanded;
            CaptureExpansion(node.Children, state);
        }
    }

    public static void RestoreExpansion(
        IEnumerable<MemoryWorkspaceNode> roots,
        IReadOnlyDictionary<string, bool> state)
    {
        foreach (var node in roots)
        {
            if (state.TryGetValue(node.Id, out var expanded)) node.IsExpanded = expanded;
            RestoreExpansion(node.Children, state);
        }
    }

    public static void RefreshAggregates(IEnumerable<MemoryWorkspaceNode> roots)
    {
        foreach (var node in roots)
        {
            node.RefreshAggregate();
            RefreshAggregates(node.Children);
        }
    }

    private static IEnumerable<MemoryWorkspaceNode> Flatten(
        IEnumerable<MemoryWorkspaceNode> nodes,
        int depth)
    {
        foreach (var node in nodes)
        {
            node.SetDepth(depth);
            yield return node;
            if (!node.IsExpanded) continue;
            foreach (var child in Flatten(node.Children, depth + 1)) yield return child;
        }
    }
}
