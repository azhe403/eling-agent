namespace Eling.Core.Workspaces;

/// <summary>
/// Which workspace-tree nodes the user has opened and which ones they have
/// closed. Both lists hold node ids, and a node id is stable across the three
/// arrangements, so the same shape applies to a flat, a repository and a
/// folder list.
/// </summary>
/// <param name="Expanded">
/// Nodes the user explicitly opened. A node nobody touched keeps the default
/// for its kind (a repository with several checkouts starts open).
/// </param>
/// <param name="Collapsed">
/// Nodes the user explicitly closed. Without this list a collapsed repository
/// would reopen on the next visit.
/// </param>
public sealed record WorkspaceExpansion(
    IReadOnlyList<string> Expanded,
    IReadOnlyList<string> Collapsed);
