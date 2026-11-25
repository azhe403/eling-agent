namespace Eling.Backend.Dtos;

/// <summary>
/// Document list grouped by topic.
/// </summary>
public sealed record DocListDto(
    IReadOnlyList<DocTopicGroupDto> Topics,
    int Total);