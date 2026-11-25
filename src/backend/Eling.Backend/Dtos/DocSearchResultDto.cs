namespace Eling.Backend.Dtos;

/// <summary>Search result DTO with rank from the global index.</summary>
public sealed record DocSearchResultDto(
    string DocId,
    string WorkspaceId,
    string Path,
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
        result.WorkspaceId,
        result.Path,
        result.Kind.ToDto(),
        result.Title,
        result.Status,
        result.Topic,
        result.StepsTotal,
        result.StepsDone,
        result.Rank);
}
