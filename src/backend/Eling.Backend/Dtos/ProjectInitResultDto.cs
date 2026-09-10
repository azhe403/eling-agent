namespace Eling.Backend.Dtos;

public sealed record ProjectInitResultDto(
    string Status,
    string? HeadRoot,
    IReadOnlyCollection<string> Chain,
    string? GitignoreWarning = null);