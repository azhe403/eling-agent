namespace Eling.Backend.Dtos;

/// <summary>
/// Search result DTO with rank.
/// </summary>
public sealed record DocSearchResultDto(
    string DocId,
    DocOwnershipDto Ownership,
    DocOriginDto Origin,
    string RelPath,
    DocKindDto Kind,
    string Title,
    int Status,
    string Topic,
    int StepsTotal,
    int StepsDone,
    double Rank)
{
    public static DocSearchResultDto From(Eling.Core.Docs.DocSearchResult result) => new(
        result.DocId,
        result.Ownership.ToDto(),
        result.Origin.ToDto(),
        result.RelPath,
        result.Kind.ToDto(),
        result.Title,
        result.Status,
        result.Topic,
        result.StepsTotal,
        result.StepsDone,
        result.Rank);
}