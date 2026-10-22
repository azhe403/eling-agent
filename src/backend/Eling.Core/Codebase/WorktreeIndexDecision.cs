namespace Eling.Core.Codebase;

/// <summary>
/// What a workspace decides about the indexing state of the repository it
/// belongs to.
/// </summary>
public enum WorktreeIndexKind
{
    /// <summary>
    /// The workspace is not a linked worktree: it is the main checkout, a
    /// subfolder of one, or not in a repository at all.
    /// </summary>
    NotAWorktree,

    /// <summary>
    /// The workspace is a linked worktree, but the main checkout's index is
    /// not active, so there is nothing to inherit.
    /// </summary>
    RepositoryIndexInactive,

    /// <summary>
    /// The workspace is a linked worktree of a repository whose index is
    /// active, so the worktree inherits that state.
    /// </summary>
    Inherit,
}

/// <summary>
/// The decision a workspace makes about inheriting its repository's indexing
/// state, with the main checkout the decision was made about.
/// </summary>
public readonly record struct WorktreeIndexDecision(
    WorktreeIndexKind Kind,
    string? RepositoryRoot)
{
    /// <summary>
    /// True when the workspace may build its index without asking again.
    /// </summary>
    public bool Inherits => Kind == WorktreeIndexKind.Inherit;
}
