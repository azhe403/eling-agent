using System.IO;

namespace Eling.Core.Workspaces;

/// <summary>
/// The single owner of the workspace-list arrangement rules. Pure: rows in,
/// nodes out, no I/O, no environment. Both the backend coordinator and the
/// desktop client consume this so the three client-side copies that used to
/// exist can be deleted.
/// </summary>
public static class WorkspaceTree
{
    public static IReadOnlyList<WorkspaceTreeNode> Build(
        IReadOnlyList<WorkspaceTreeRow> rows,
        WorkspaceGroupMode mode)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return mode switch
        {
            WorkspaceGroupMode.Repo => RepositoryNodes(rows),
            WorkspaceGroupMode.Folder => FolderNodes(rows),
            _ => FlatNodes(rows),
        };
    }

    private static string PathKey(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

    private static bool IsAncestor(string ancestor, string descendant)
    {
        var a = PathKey(ancestor);
        var d = PathKey(descendant);
        if (a == d) return false;
        return d.StartsWith(a + "/", StringComparison.Ordinal);
    }

    private sealed record RepositoryGroup(
        string Key,
        string Path,
        WorkspaceTreeRow? Primary,
        List<WorkspaceTreeRow> Members);

    private static List<RepositoryGroup> RepositoryGroups(IReadOnlyList<WorkspaceTreeRow> rows)
    {
        var buckets = new Dictionary<string, List<WorkspaceTreeRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = PathKey(row.RepositoryRoot ?? row.Root);
            if (!buckets.TryGetValue(key, out var list))
            {
                list = [];
                buckets[key] = list;
            }
            list.Add(row);
        }

        var result = new List<RepositoryGroup>();
        foreach (var members in buckets.Values)
        {
            var primary = members.FirstOrDefault(m =>
                PathKey(m.Root) == PathKey(m.RepositoryRoot ?? m.Root));
            var path = primary?.Root ?? members[0].RepositoryRoot ?? members[0].Root;
            result.Add(new RepositoryGroup(PathKey(path), path, primary, members));
        }
        return result;
    }

    private static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static WorkspaceTreeNode WorkspaceNode(WorkspaceTreeRow row, bool isWorktree)
    {
        return new WorkspaceTreeNode(
            $"ws:{PathKey(row.Root)}",
            NameOf(row.Root),
            row.Root,
            row.Root,
            row.Alive,
            [row.Root],
            false,
            isWorktree,
            []);
    }

    private static WorkspaceTreeNode RepositoryNode(RepositoryGroup group)
    {
        var others = group.Members.Where(m => m != group.Primary).ToList();
        var children = others
            .OrderBy(r => NameOf(r.Root), StringComparer.Ordinal)
            .Select(r => WorkspaceNode(r, true))
            .ToList();
        var alive = group.Members.Any(m => m.Alive);
        return new WorkspaceTreeNode(
            $"repo:{group.Key}",
            NameOf(group.Path),
            group.Path,
            group.Primary?.Root,
            alive,
            [.. group.Members.Select(m => m.Root)],
            true,
            false,
            children);
    }

    private static List<WorkspaceTreeNode> FlatNodes(IReadOnlyList<WorkspaceTreeRow> rows)
    {
        return rows
            .Select(r => WorkspaceNode(r, false))
            .OrderBy(n => n.Label, StringComparer.Ordinal)
            .ToList();
    }

    private static List<WorkspaceTreeNode> RepositoryNodes(IReadOnlyList<WorkspaceTreeRow> rows)
    {
        return RepositoryGroups(rows)
            .Select(RepositoryNode)
            .OrderBy(n => n.Label, StringComparer.Ordinal)
            .ToList();
    }

    private static RepositoryGroup? NearestContainer(
        IReadOnlyList<RepositoryGroup> groups,
        RepositoryGroup group)
    {
        RepositoryGroup? best = null;
        foreach (var candidate in groups)
        {
            if (ReferenceEquals(candidate, group)) continue;
            if (!IsAncestor(candidate.Path, group.Path)) continue;
            if (best is null || IsAncestor(best.Path, candidate.Path)) best = candidate;
        }
        return best;
    }

    private static List<WorkspaceTreeNode> FolderNodes(IReadOnlyList<WorkspaceTreeRow> rows)
    {
        var groups = RepositoryGroups(rows);
        var byKey = groups.ToDictionary(g => g.Key, g => g);
        var nested = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var parent = NearestContainer(groups, group);
            if (parent is null) continue;
            if (!nested.TryGetValue(parent.Key, out var list))
            {
                list = [];
                nested[parent.Key] = list;
            }
            list.Add(group.Key);
        }

        WorkspaceTreeNode Build(RepositoryGroup g)
        {
            var baseNode = RepositoryNode(g);
            if (!nested.TryGetValue(g.Key, out var childKeys) || childKeys.Count == 0)
            {
                return baseNode;
            }
            // TS parity: [...base.children, ...descendants].sort(byLabel)
            var descendants = childKeys
                .Select(key => Build(byKey[key]))
                .OrderBy(n => n.Label, StringComparer.Ordinal);
            var merged = baseNode.Children
                .Concat(descendants)
                .OrderBy(n => n.Label, StringComparer.Ordinal)
                .ToList();
            return baseNode with { Children = merged };
        }

        return groups
            .Where(g => NearestContainer(groups, g) is null)
            .Select(Build)
            .OrderBy(n => n.Label, StringComparer.Ordinal)
            .ToList();
    }
}
