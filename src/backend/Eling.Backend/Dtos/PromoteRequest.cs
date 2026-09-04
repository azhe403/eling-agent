namespace Eling.Backend.Dtos;

public record PromoteRequest(
    string Id,
    string SourceProjectRoot,
    bool Move = false);