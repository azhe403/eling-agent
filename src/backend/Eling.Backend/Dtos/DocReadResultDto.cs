namespace Eling.Backend.Dtos;

/// <summary>
/// Document read result with sections and optional heading tree.
/// </summary>
public sealed record DocReadResultDto(
    DocDto Document,
    IReadOnlyList<DocSectionDto> Sections,
    IReadOnlyList<DocRefDto> References,
    IReadOnlyList<HeadingTreeDto>? HeadingTree = null);