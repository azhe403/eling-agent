namespace Eling.Core.Projects;

/// <summary>
/// The catalogue of workspace folders Eling has seen.
/// </summary>
/// <remarks>
/// Replaces the per-process registry. A row is a folder, keyed by its workspace
/// root, and it survives the process that wrote it — that survival is the whole
/// point, since a codebase index is a readable file long after its process exits.
/// </remarks>
public interface IWorkspacesRegistry
{
    /// <summary>
    /// Records a workspace. First sight inserts a row; a later registration
    /// refreshes <c>last_seen_at</c> only, so the first-seen date is never
    /// rewritten. Never throws — a failure here must not stop a runtime starting.
    /// </summary>
    void Record(RegisteredWorkspace workspace);

    /// <summary>
    /// Every workspace ever registered, most recently seen first. Includes
    /// folders that no longer exist on disk — deciding what a missing folder
    /// means belongs to the caller, not to storage.
    /// </summary>
    Task<IReadOnlyList<RegisteredWorkspace>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The workspace whose root matches, or null.
    /// </summary>
    Task<RegisteredWorkspace?> FindByRootAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a workspace entry from the registry by its workspace root.
    /// </summary>
    Task<bool> DeleteByRootAsync(string root, CancellationToken cancellationToken = default);
}