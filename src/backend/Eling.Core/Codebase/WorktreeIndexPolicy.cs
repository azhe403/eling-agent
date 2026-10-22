using Eling.Core.Git;

namespace Eling.Core.Codebase;

/// <summary>
/// Decides whether a workspace inherits the indexing state of its repository.
/// </summary>
/// <remarks>
/// A linked worktree is a checkout of a repository that is already indexed, so
/// asking the user for consent a second time would be noise: the repository
/// was indexed once, on purpose, and the worktree holds the same code. The
/// worktree keeps its own index — one database per workspace root, exactly as
/// every other workspace has — it only starts without the extra question.
/// <para>
/// The main checkout itself inherits nothing: it is the root that decides.
/// </para>
/// </remarks>
public static class WorktreeIndexPolicy
{
    /// <summary>
    /// Decides whether <paramref name="workspaceRoot"/> inherits the indexing
    /// state of the repository it belongs to.
    /// </summary>
    /// <param name="workspaceRoot">The workspace root to judge.</param>
    /// <param name="isRepositoryIndexActive">
    /// Asks whether a repository root's index is active. Defaults to reading
    /// that root's index; the seam exists so the decision itself can be tested
    /// without a store on disk.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<WorktreeIndexDecision> EvaluateAsync(
        string? workspaceRoot,
        Func<string, CancellationToken, Task<bool>>? isRepositoryIndexActive = null,
        CancellationToken ct = default)
    {
        var probe = isRepositoryIndexActive ?? CodebaseIndexService.IsIndexInitializedAtAsync;

        var repositoryRoot = ResolveRepositoryRoot(workspaceRoot);
        if (repositoryRoot is null) return new WorktreeIndexDecision(WorktreeIndexKind.NotAWorktree, null);

        var active = await probe(repositoryRoot, ct).ConfigureAwait(false);
        var kind = active ? WorktreeIndexKind.Inherit : WorktreeIndexKind.RepositoryIndexInactive;
        return new WorktreeIndexDecision(kind, repositoryRoot);
    }

    /// <summary>
    /// The main checkout of the repository the workspace belongs to, when the
    /// workspace sits in a linked worktree of it. Null when the workspace is
    /// the main checkout, a subfolder of one, or not in a repository at all.
    /// </summary>
    public static string? ResolveRepositoryRoot(string? workspaceRoot)
    {
        var location = GitRepositoryResolver.ResolveRepository(workspaceRoot);
        return location.IsLinkedWorktree ? location.Root : null;
    }
}
