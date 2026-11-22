using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Staleness finding DTO.
/// </summary>
public sealed record StalenessFindingDto(
    string DocId,
    string Title,
    string RelPath,
    StalenessKindDto Kind,
    string Message,
    string? Anchor)
{
    public static StalenessFindingDto From(StalenessFinding finding) => new(
        finding.DocId,
        finding.Title,
        finding.RelPath,
        finding.Kind.ToDto(),
        finding.Message,
        finding.Anchor
    );
}