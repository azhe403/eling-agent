namespace Eling.Backend.Dtos;

/// <summary>
/// A resolved rebuild request: the effective scope name, the project roots
/// that will actually be indexed, and the roots dropped because no index DB
/// exists for them yet. Produced by the endpoint from the same scope parsing
/// <c>/status</c> and <c>/search</c> use, so one selector means the same thing
/// everywhere. <paramref name="Full"/> selects a full re-index over an
/// incremental pass.
/// </summary>
public sealed record CodebaseRebuildRequest(
    string Scope,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Skipped,
    bool Full);
