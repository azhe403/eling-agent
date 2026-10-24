namespace Eling.Desktop.Models;

public sealed record ProjectCodebaseStatusDto(
    string WorkspaceRoot,
    int Files,
    int Chunks,
    string? LastIndexedAt,
    string? DbPath,
    long DbSizeBytes);
