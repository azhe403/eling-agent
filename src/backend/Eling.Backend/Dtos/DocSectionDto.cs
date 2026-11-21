namespace Eling.Backend.Dtos;

/// <summary>
/// Document section DTO.
/// </summary>
public sealed record DocSectionDto(
    string DocId,
    string Anchor,
    string Heading,
    int Level,
    int LineStart,
    int LineEnd,
    int Status,
    string? Annotation,
    int StepsTotal,
    int StepsDone)
{
    public static DocSectionDto From(Eling.Core.Docs.DocSection section) => new(
        section.DocId.Value,
        section.Anchor,
        section.Heading,
        section.Level,
        section.LineStart,
        section.LineEnd,
        section.Status.Value,
        section.Annotation,
        section.StepsTotal,
        section.StepsDone);
}