namespace Eling.Core.Docs;

/// <summary>
/// Derived, disposable global index. Audit and metadata are canonical elsewhere.
/// </summary>
public interface IDocsIndex
{
    Task IndexAsync(Doc doc, IReadOnlyList<DocSection> sections, IReadOnlyList<DocRef> refs, string? body = null);
    Task DeleteAsync(string docId);
    Task RebuildAsync(IEnumerable<DocRebuildItem> items);
    Task<Doc?> GetAsync(string docId);
    Task<DocSection?> GetSectionAsync(string docId, string anchor);
    Task<IReadOnlyList<DocSection>> GetSectionsAsync(string docId);
    Task<IReadOnlyList<DocRef>> GetRefsAsync(string docId);
    Task<IReadOnlyList<DocRelation>> GetRelationsAsync(string docId, bool asSource);
    Task UpsertRelationAsync(DocRelation relation);
    Task DeleteRelationAsync(string sourceId, string targetId, RelationKind kind);
    Task<IReadOnlyList<DocSearchResult>> SearchAsync(
        string query,
        string? workspaceId = null,
        DocKind? kind = null,
        string? topic = null,
        string? pathPrefix = null,
        int limit = 10);
    Task<IReadOnlyList<DocListItem>> ListAsync(
        string? workspaceId = null,
        DocKind? kind = null,
        string? topic = null,
        string sort = "name",
        string order = "asc");
}
