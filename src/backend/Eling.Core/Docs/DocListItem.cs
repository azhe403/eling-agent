namespace Eling.Core.Docs;

/// <summary>
/// List item.
/// </summary>
public sealed record DocListItem(
    string DocId,
    DocOwnership Ownership,
    DocOrigin Origin,
    string RelPath,
    DocKind Kind,
    string Title,
    int Status,
    string Topic,
    int Order,
    int StepsTotal,
    int StepsDone);