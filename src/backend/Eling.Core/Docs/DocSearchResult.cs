namespace Eling.Core.Docs;

/// <summary>Search result with workspace and rank from the single global index.</summary>
public sealed record DocSearchResult(
    string DocId,
    string WorkspaceId,
    string Path,
    DocKind Kind,
    string Title,
    int Status,
    string Topic,
    int StepsTotal,
    int StepsDone,
    double Rank);
