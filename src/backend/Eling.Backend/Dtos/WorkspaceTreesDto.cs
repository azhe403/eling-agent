namespace Eling.Backend.Dtos;

/// <summary>
/// The workspace list, pre-arranged in all three view modes, so a client
/// switch never costs a request and neither client keeps a per-mode cache.
/// </summary>
/// <remarks>
/// All three arrangements describe the same <see cref="Rows"/>. A client picks
/// one and renders it; none of them derives grouping from the rows itself.
/// </remarks>
public sealed record WorkspaceTreesDto(
    IReadOnlyList<WorkspaceTreeRowDto> Rows,
    IReadOnlyList<WorkspaceTreeNodeDto> Flat,
    IReadOnlyList<WorkspaceTreeNodeDto> Repo,
    IReadOnlyList<WorkspaceTreeNodeDto> Folder);

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
