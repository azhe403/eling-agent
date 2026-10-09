namespace Eling.Backend.Dtos;

/// <summary>
/// Transient in-memory snapshot of codebase status for a project workspace.
/// Computed on-the-fly and never persisted in any database.
/// </summary>
public sealed record ProjectCodebaseStatusDto(
    string WorkspaceRoot,
    int Files,
    int Chunks,
    string? LastIndexedAt,
    string? DbPath);
