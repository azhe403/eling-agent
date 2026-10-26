namespace Eling.Core.Workspaces;

/// <summary>
/// One node in a workspace list tree.
/// </summary>
/// <param name="Id">Stable identity used as the React/Avalonia key.</param>
/// <param name="Label">Display name.</param>
/// <param name="Path">The path this node stands for.</param>
/// <param name="Root">
/// The workspace root this node selects, or null when the node stands for a
/// repository whose main checkout is not itself a registered workspace.
/// </param>
/// <param name="Alive">Whether any member workspace is live.</param>
/// <param name="Roots">Every workspace root this node covers (the checkbox scope).</param>
/// <param name="IsRepository">True when the node stands for a git repository.</param>
/// <param name="IsWorktree">True when the node's own workspace is a linked worktree.</param>
/// <param name="Children">Nested nodes.</param>
public sealed record WorkspaceTreeNode(
    string Id,
    string Label,
    string Path,
    string? Root,
    bool Alive,
    IReadOnlyList<string> Roots,
    bool IsRepository,
    bool IsWorktree,
    IReadOnlyList<WorkspaceTreeNode> Children);