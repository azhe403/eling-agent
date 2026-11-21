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
    string? Anchor);