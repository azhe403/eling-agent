using Eling.Core.Scope;

namespace Eling.Backend.Scope;

/// <summary>
/// Reads and mutates the machine-local project-scope policy
/// (<c>&lt;user-scope&gt;/config/project-policy.json</c>). Asynchronous, matching
/// the async storage engine (<c>FileSystemMemoryStorage</c>/<c>SqliteMemoryIndex</c>).
/// Implementations own all timestamp bookkeeping per the design: <c>createdAt</c>
/// is set once and never changes; <c>updatedAt</c> moves only when the effective
/// policy changes, so an idempotent mutation is a no-op.
/// </summary>
public interface IProjectScopePolicyStore
{
    /// <summary>Load the current policy. Missing or corrupt files yield the default policy.</summary>
    Task<ProjectScopePolicy> LoadAsync();

    /// <summary>Insert or update an exact project-root decision.</summary>
    Task<ProjectScopePolicy> SetProjectAsync(string root, ProjectScopeDecision decision);

    /// <summary>Insert or update a glob path rule.</summary>
    Task<ProjectScopePolicy> SetPatternAsync(string glob, ProjectScopeDecision decision);

    /// <summary>Set the fallback decision used when nothing more specific matches.</summary>
    Task<ProjectScopePolicy> SetDefaultAsync(ProjectScopeDecision decision);

    /// <summary>Remove an exact project-root decision.</summary>
    /// <exception cref="KeyNotFoundException">No entry exists for <paramref name="root"/>.</exception>
    Task<ProjectScopePolicy> ClearProjectAsync(string root);

    /// <summary>Remove a glob path rule.</summary>
    /// <exception cref="KeyNotFoundException">No pattern matches <paramref name="glob"/>.</exception>
    Task<ProjectScopePolicy> ClearPatternAsync(string glob);
}
