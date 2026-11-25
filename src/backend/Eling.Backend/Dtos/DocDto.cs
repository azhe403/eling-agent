namespace Eling.Backend.Dtos;

/// <summary>Document data transfer object — no ownership or origin.</summary>
public sealed record DocDto(
    string Id,
    string WorkspaceId,
    string Path,
    DocKindDto Kind,
    string Title,
    int Status,
    string Topic,
    int Order,
    bool Archived,
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
        doc.WorkspaceId,
        doc.Path,
        doc.Kind.ToDto(),
        doc.Title,
        doc.Status.Value,
        doc.Topic,
        doc.Order,
        doc.Archived,
        doc.SupersededBy,
        doc.StepsTotal,
        doc.StepsDone,
        doc.FileHash,
        doc.MTime,
        doc.IndexedAt,
        doc.FrontMatterError,
        doc.FrontMatterErrorMessage);
}
