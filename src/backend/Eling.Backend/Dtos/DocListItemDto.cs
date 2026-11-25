namespace Eling.Backend.Dtos;

/// <summary>List item DTO with workspace and archive state.</summary>
public sealed record DocListItemDto(
    string DocId,
    string WorkspaceId,
    string Path,
    DocKindDto Kind,
    string Title,
    int Status,
    string Topic,
    int Order,
    bool Archived,
    int StepsTotal,
    int StepsDone,
    string MTime)
{
    public static DocListItemDto From(Eling.Core.Docs.DocListItem item) => new(
        item.DocId,
        item.WorkspaceId,
        item.Path,
        item.Kind.ToDto(),
        item.Title,
        item.Status,
        item.Topic,
        item.Order,
        item.Archived,
        item.StepsTotal,
        item.StepsDone,
        item.MTime);
}
