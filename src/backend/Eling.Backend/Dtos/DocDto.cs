namespace Eling.Backend.Dtos;

/// <summary>
/// Document data transfer object with provenance (ownership, origin).
/// </summary>
public sealed record DocDto(
    string Id,
    DocOwnershipDto Ownership,
    DocOriginDto Origin,
    string RelPath,
    DocKindDto Kind,
    string Title,
    int Status,
    string Topic,
    int Order,
    string? SupersededBy,
    int StepsTotal,
    int StepsDone,
    string FileHash,
    DateTimeOffset MTime,
    DateTimeOffset IndexedAt,
    bool FrontMatterError,
    string? FrontMatterErrorMessage)
{
    public static DocDto From(Eling.Core.Docs.Doc doc) => new(
        doc.Id.Value,
        doc.Ownership.ToDto(),
        doc.Origin.ToDto(),
        doc.RelPath,
        doc.Kind.ToDto(),
        doc.Title,
        doc.Status.Value,
        doc.Topic,
        doc.Order,
        doc.SupersededBy,
        doc.StepsTotal,
        doc.StepsDone,
        doc.FileHash,
        doc.MTime,
        doc.IndexedAt,
        doc.FrontMatterError,
        doc.FrontMatterErrorMessage);
}