using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Staleness report DTO.
/// </summary>
public sealed record StalenessReportDto(
    IReadOnlyList<StalenessFindingDto> Findings,
    DateTimeOffset GeneratedAt)
{
    public static StalenessReportDto From(StalenessReport report) => new(
        report.Findings.Select(StalenessFindingDto.From).ToList().AsReadOnly(),
        report.GeneratedAt
    );
}