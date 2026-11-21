namespace Eling.Backend.Dtos;

/// <summary>
/// List item DTO.
/// </summary>
public sealed record DocListItemDto(
    string DocId,
    DocOwnershipDto Ownership,
    DocOriginDto Origin,
    string RelPath,
    DocKindDto Kind,
    string Title,
    int Status,
    string Topic,
    int Order,
    int StepsTotal,
    int StepsDone)
{
    public static DocListItemDto From(Eling.Core.Docs.DocListItem item) => new(
        item.DocId,
        item.Ownership.ToDto(),
        item.Origin.ToDto(),
        item.RelPath,
        item.Kind.ToDto(),
        item.Title,
        item.Status,
        item.Topic,
        item.Order,
        item.StepsTotal,
        item.StepsDone);
}