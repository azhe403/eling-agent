namespace Eling.Backend.Dtos;

public record CopyRequest(
    string Id,
    string SourceScope,
    string? SourceProjectRoot,
    string TargetProjectRoot,
    bool Move = false);