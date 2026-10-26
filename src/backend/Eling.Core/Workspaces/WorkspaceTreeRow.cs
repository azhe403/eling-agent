namespace Eling.Core.Workspaces;

/// <summary>
/// One deduplicated workspace row fed to <see cref="WorkspaceTree"/>.
/// </summary>
/// <param name="Root">The workspace folder (codebase root).</param>
/// <param name="Alive">Whether at least one process is live for this workspace.</param>
/// <param name="RepositoryRoot">
/// The main worktree root of the repository the workspace belongs to, or null.
/// </param>
public sealed record WorkspaceTreeRow(
    string Root,
    bool Alive,
    string? RepositoryRoot);