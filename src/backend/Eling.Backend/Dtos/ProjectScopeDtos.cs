namespace Eling.Backend.Dtos;

/// <summary>
/// One project scope as the dashboard's memory picker needs it: where it lives,
/// and whether a save may target it from here.
/// </summary>
/// <remarks>
/// Deliberately says nothing about chain position. "Own", "ancestor" and "depth"
/// describe how the scope relates to the *calling* workspace, which is the
/// registry's internal bookkeeping — a reader browsing memories does not care
/// whether a folder was level 1 or level 0, they care that it is somewhere they
/// can read.
/// </remarks>
/// <param name="Root">
/// The scope directory, native separators and original casing, for display and
/// as the <c>projectRoot</c> query value.
/// </param>
/// <param name="Name">The directory's last segment, for the dropdown label.</param>
/// <param name="Writable">
/// Whether a save targeting this scope is legal from here. An ancestor is
/// writable only when this workspace has its own scope — the rule
/// <c>ResolveAncestorProjectRoot</c> enforces. A scope outside this workspace's
/// chain is never a write target, but it is always readable, which is why it is
/// listed at all.
/// </param>
/// <param name="HasLiveRuntime">Whether a process is currently serving this scope.</param>
public sealed record ProjectScopeDto(
    string Root,
    string Name,
    bool Writable,
    bool HasLiveRuntime);

/// <summary>The scope listing: this workspace's chain first, then other known scopes.</summary>
/// <param name="Cwd">The workspace the listing was resolved for.</param>
/// <param name="Scopes">Chain scopes first, then other known scopes.</param>
public sealed record ProjectScopeListDto(
    string Cwd,
    IReadOnlyCollection<ProjectScopeDto> Scopes);