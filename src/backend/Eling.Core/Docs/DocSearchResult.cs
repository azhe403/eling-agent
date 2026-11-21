namespace Eling.Core.Docs;

/// <summary>
/// Search result.
/// </summary>
public sealed record DocSearchResult(
    string DocId,
    DocOwnership Ownership,
    DocOrigin Origin,
    string RelPath,
    DocKind Kind,
    string Title,
    int Status,
    string Topic,
    int StepsTotal,
    int StepsDone,
    double Rank);