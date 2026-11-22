using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Document read result with sections and optional heading tree.
/// </summary>
public sealed record DocReadResultDto(
    DocDto Document,
    IReadOnlyList<DocSectionDto> Sections,
    IReadOnlyList<DocRefDto> References,
    IReadOnlyList<HeadingTreeDto>? HeadingTree = null,
    string? Content = null)
{
    public static DocReadResultDto From(DocReadResult result) => new(
        DocDto.From(result.Document),
        result.Sections?.Select(DocSectionDto.From).ToList().AsReadOnly() ?? Array.Empty<DocSectionDto>().ToList().AsReadOnly(),
        result.References?.Select(DocRefDto.From).ToList().AsReadOnly() ?? Array.Empty<DocRefDto>().ToList().AsReadOnly(),
        null, // HeadingTree not implemented yet
        result.Content
    );
}