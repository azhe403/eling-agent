namespace Eling.Backend.Dtos;

/// <summary>
/// Response for doc_promote.
/// </summary>
public sealed record DocPromoteResponse(
    DocDto Document)
{
    public static DocPromoteResponse From(Eling.Core.Docs.Doc doc) =>
        new(DocDto.From(doc));
}