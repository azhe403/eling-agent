namespace Eling.Backend.Dtos;

/// <summary>
/// Transient in-memory snapshot of codebase status for a project workspace.
/// Computed on-the-fly and never persisted in any database.
/// </summary>
/// <param name="WorkspaceRoot">The workspace folder the index belongs to.</param>
/// <param name="Files">How many files the index holds.</param>
/// <param name="Chunks">How many chunks the index holds.</param>
/// <param name="LastIndexedAt">When the index was last written, or null if never.</param>
/// <param name="DbPath">Where the index file lives.</param>
/// <param name="DbSizeBytes">
/// What the index occupies on disk: the database file plus its write-ahead
/// log. Zero when the workspace has no index yet.
/// </param>
public sealed record ProjectCodebaseStatusDto(
    string WorkspaceRoot,
    int Files,
    int Chunks,
    string? LastIndexedAt,
    string? DbPath,
    long DbSizeBytes);
