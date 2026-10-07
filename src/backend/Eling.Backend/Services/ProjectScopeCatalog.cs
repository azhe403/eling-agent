using Eling.Backend.Dtos;
using Eling.Core;
using Eling.Core.Projects;
using Eling.Core.Scope;

namespace Eling.Backend.Services;

/// <summary>
/// Resolves the set of memory scopes reachable from a workspace, for the
/// dashboard's scope picker.
/// </summary>
/// <remarks>
/// The picker used to read <c>/api/coordinator/runtimes</c>, which derives from
/// live processes. That is the wrong source for memory: a valid memory scope is
/// the <c>.eling</c> chain on disk plus what the registry has recorded, and the
/// two differ in exactly the cases that matter. An ancestor scope nobody is
/// currently running in is still a scope a save may target, and it was absent
/// from the picker — and unreachable, because
/// <c>TryResolveMemoryServiceByScopeRoot</c> only matched alive runtimes.
/// <para>
/// Codebase keeps the runtime list. Its identity is the workspace, it is
/// already persistent on disk, and it must never climb to a <c>.eling</c>
/// ancestor — a different question from this one.
/// </para>
/// <para>
/// A scope is identified by its location, so nothing here reads or writes an
/// identity file inside the project. All machine-local state stays in the global
/// data root, and the project's working tree only ever gains
/// <c>.eling/memories/</c> — the one thing meant to be committed.
/// </para>
/// </remarks>
public sealed class ProjectScopeCatalog(RuntimeRegistry registry)
{
    private readonly RuntimeRegistry _registry = registry;

    /// <summary>
    /// The scopes for <paramref name="cwd"/>: this workspace's chain first,
    /// nearest level 0, then any other scope the registry knows.
    /// </summary>
    public async Task<ProjectScopeListDto> ListAsync(string cwd, CancellationToken cancellationToken = default)
    {
        var fullCwd = string.IsNullOrWhiteSpace(cwd)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(cwd);

        var chain = ScopeChain.Discover(fullCwd);
        var known = await _registry.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false);

        // Relation is computed here and never stored: it depends on which
        // workspace is asking, so persisting it would be wrong from any other.
        var liveRoots = new HashSet<string>(
            _registry.Alive()
                .Where(r => !string.IsNullOrWhiteSpace(r.WorkspaceRoot))
                .Select(r => RootKeys.Of(r.WorkspaceRoot)),
            StringComparer.Ordinal);

        var ordered = new List<ProjectScopeDto>();
        var placed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var level in chain.Levels)
        {
            placed.Add(RootKeys.Of(level.Root));
            ordered.Add(ToDto(level.Root, chain.HasOwnScope, liveRoots));
        }

        // Remembered workspaces join as scopes only when the folder still holds a
        // .eling. The catalogue records folders, not memory scopes — a workspace
        // with an index but no .eling must not appear in the memory picker, which
        // is the exact confusion this separation exists to prevent.
        foreach (var workspace in known)
        {
            if (!Directory.Exists(Path.Combine(workspace.WorkspaceRoot, ProjectScope.DataDirectoryName)))
            {
                continue;
            }

            if (!placed.Add(RootKeys.Of(workspace.WorkspaceRoot))) continue;
            ordered.Add(ToDto(workspace.WorkspaceRoot, hasOwnScope: false, liveRoots));
        }

        return new ProjectScopeListDto(fullCwd, ordered.AsReadOnly());
    }

    private static ProjectScopeDto ToDto(
        string root,
        bool hasOwnScope,
        HashSet<string> liveRoots)
    {
        var rootKey = RootKeys.Of(root);
        return new ProjectScopeDto(
            root,
            NameOf(root),
            // Same rule ScopedMemoryService.ResolveAncestorProjectRoot applies:
            // a chain scope is a write target only when this workspace has its
            // own. A scope from elsewhere is never a write target — but it is
            // always readable, which is why it is listed.
            hasOwnScope,
            liveRoots.Contains(rootKey));
    }

    private static string NameOf(string root)
    {
        var parts = root.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : root;
    }
}