namespace Eling.Backend.Dtos;

/// <summary>
/// The workspace list, pre-arranged for ONE view mode at a time. The client
/// picks a mode (<c>flat</c>, <c>repo</c>, or <c>folder</c>) and receives the
/// rows plus that single arrangement, so switching mode is a fresh request —
/// never client-side grouping.
/// </summary>
/// <remarks>
/// <see cref="Rows"/> is the flat, deduplicated list; <see cref="Nodes"/> is
/// the tree for the requested mode. A client renders <see cref="Nodes"/> and
/// joins them against <see cref="Rows"/> by root; it never derives grouping
/// from the rows itself.
/// </remarks>
/// <param name="Rows">The deduplicated workspaces the arrangement was built from.</param>
/// <param name="Group">The mode that was honoured: <c>flat</c>, <c>repo</c>, or <c>folder</c>.</param>
/// <param name="Nodes">The arrangement for <paramref name="Group"/>.</param>
public sealed record WorkspaceTreesDto(
    IReadOnlyList<WorkspaceTreeRowDto> Rows,
    string Group,
    IReadOnlyList<WorkspaceTreeNodeDto> Nodes);

/// <summary>
/// One deduplicated workspace, as the list needs it before grouping.
/// </summary>
/// <param name="Root">The workspace folder — the identity every client selects by.</param>
/// <param name="Name">The folder's last segment, the label both clients show.</param>
/// <param name="RepositoryRoot">
/// The main worktree root of the repository the workspace belongs to, or null
/// when it is not inside a git repository.
/// </param>
/// <param name="Alive">Whether a process is running for this workspace.</param>
/// <param name="ProcessCount">
/// How many live processes collapsed into this row; 0 for a remembered
/// workspace whose process is gone.
/// </param>
public sealed record WorkspaceTreeRowDto(
    string Root,
    string Name,
    string? RepositoryRoot,
    bool Alive,
    int ProcessCount);

/// <summary>
/// A node of one arrangement: a workspace, or a repository holding its
/// worktrees.
/// </summary>
/// <param name="Id">Stable across arrangements — <c>ws:{key}</c> or <c>repo:{key}</c>.</param>
/// <param name="Label">What the client shows for this node.</param>
/// <param name="Path">The repository root for a repository node, the folder for a workspace.</param>
/// <param name="Root">
/// The workspace this node stands for, or null when the node is a repository
/// with no registered main checkout.
/// </param>
/// <param name="Alive">True when any workspace this node covers has a running process.</param>
/// <param name="Roots">
/// Every workspace root this node covers — the checkbox scope. A repository
/// node covers its worktrees, so ticking it ticks all of them.
/// </param>
/// <param name="IsRepository">True for a repository group.</param>
/// <param name="IsWorktree">True for a linked worktree nested under its main checkout.</param>
/// <param name="Children">Nested nodes, ordered by label.</param>
public sealed record WorkspaceTreeNodeDto(
    string Id,
    string Label,
    string Path,
    string? Root,
    bool Alive,
    IReadOnlyList<string> Roots,
    bool IsRepository,
    bool IsWorktree,
    IReadOnlyList<WorkspaceTreeNodeDto> Children);

/// <summary>
/// The workspace tree's open/closed shape, shared by the desktop app and the
/// dashboard so both render the same tree.
/// </summary>
/// <param name="Expanded">Node ids the user explicitly opened.</param>
/// <param name="Collapsed">Node ids the user explicitly closed.</param>
public sealed record WorkspaceExpansionDto(
    IReadOnlyList<string> Expanded,
    IReadOnlyList<string> Collapsed);
