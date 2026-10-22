namespace Eling.Core.Git;

/// <summary>
/// Where a directory sits in its git repository: the main worktree's root, and
/// whether the directory is a linked worktree rather than the main checkout.
/// </summary>
/// <remarks>
/// Two questions that callers need apart. The dashboard groups workspaces by
/// repository and wants only the root. A worktree that inherits its
/// repository's indexing state has to know the marker it found was a worktree
/// pointer and not the main checkout's own <c>.git</c> directory: a subfolder
/// of the main checkout resolves to the same root, but the main checkout is
/// not a worktree of itself.
/// </remarks>
public readonly record struct GitRepositoryLocation(
    string? Root,
    bool IsLinkedWorktree);
