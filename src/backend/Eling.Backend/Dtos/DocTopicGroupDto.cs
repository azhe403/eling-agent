namespace Eling.Backend.Dtos;

/// <summary>
/// Topic group with documents.
/// </summary>
public sealed record DocTopicGroupDto(
    string Topic,
    IReadOnlyList<DocListItemDto> Documents);