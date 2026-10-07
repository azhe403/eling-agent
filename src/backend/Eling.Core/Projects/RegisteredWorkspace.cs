namespace Eling.Core.Projects;

/// <summary>
/// One workspace folder Eling has seen: the catalogue of where code and
/// memories live.
/// </summary>
/// <remarks>
/// A folder, not a process. A process is a live thing the registry tracks in
/// memory and verifies against the OS; by the time it reaches this store the
/// only durable fact left is "this folder was here". That is why there is no pid
/// and no liveness column here — neither survived contact with reality. The pid
/// was reused by the OS, and the liveness flag was written by whoever happened to
/// hold the dashboard port, could not be cleared by a process killed under a dead
/// parent, and was never read by anyone who trusted it.
/// </remarks>
/// <param name="WorkspaceId">
/// The row's identity, a ULID. It names the row; it is not what you look a
/// workspace up by — <paramref name="WorkspaceRoot"/> is, through a unique
/// constraint that also keeps this id from being re-minted on re-registration.
/// </param>
/// <param name="WorkspaceRoot">
/// The directory the backend runs in — the codebase index root, never a
/// <c>.eling</c> ancestor. One folder, one row.
/// </param>
/// <param name="HeadScopeRoot">
/// The nearest <c>.eling</c> at or above the workspace — the head of the scope
/// chain, and where memories are written. Not a project root: it can sit above
/// the repository, and for a user-home session it is the <c>UserScope</c>
/// sentinel rather than a path. Stored rather than re-resolved so a closed
/// workspace stays findable after its folder moves.
/// </param>
/// <param name="CodebaseEnabled">
/// The exclusion decision made at registration time. Stored, never recomputed:
/// changing the exclusion list later must not retroactively re-admit or drop a
/// workspace.
/// </param>
/// <param name="FirstSeenAt">When this folder was first registered. Never rewritten.</param>
/// <param name="LastSeenAt">When it was last registered or seen live.</param>
public sealed record RegisteredWorkspace(
    string WorkspaceId,
    string WorkspaceRoot,
    string HeadScopeRoot,
    bool CodebaseEnabled,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);