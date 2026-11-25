namespace Eling.Core.Docs;

/// <summary>List item with workspace and archive state.</summary>
public sealed record DocListItem(
    string DocId,
    string WorkspaceId,
    string Path,
    DocKind Kind,
    string Title,
    int Status,
    string Topic,
    int Order,
    bool Archived,
    int StepsTotal,
    int StepsDone,
    string MTime);
