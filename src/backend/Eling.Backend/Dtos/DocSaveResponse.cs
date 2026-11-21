namespace Eling.Backend.Dtos;

/// <summary>
/// Response for doc_save.
/// </summary>
public sealed record DocSaveResponse(
    DocDto Document,
    string Action) // "created" or "updated"
{
    public static DocSaveResponse From(Eling.Core.Docs.Doc doc, string action) =>
        new(DocDto.From(doc), action);
}