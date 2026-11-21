namespace Eling.Backend.Dtos;

/// <summary>
/// Staleness report DTO.
/// </summary>
public sealed record StalenessReportDto(
    IReadOnlyList<StalenessFindingDto> Findings,
    DateTimeOffset GeneratedAt);