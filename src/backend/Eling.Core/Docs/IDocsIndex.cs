namespace Eling.Core.Docs;

/// <summary>Global docs index — also the canonical store for docs metadata.</summary>
public interface IDocsIndex
{
    Task IndexAsync(Doc doc, IReadOnlyList<DocSection> sections, IReadOnlyList<DocRef> refs, string? body = null);
    Task DeleteAsync(string docId);
    Task RebuildAsync(IEnumerable<DocRebuildItem> items);
    Task<Doc?> GetAsync(string docId);
    Task<Doc?> GetByPathAsync(string workspaceId, string path, CancellationToken ct = default);
    Task<DocSection?> GetSectionAsync(string docId, string anchor);
    Task<IReadOnlyList<DocSection>> GetSectionsAsync(string docId);
    Task<IReadOnlyList<DocRef>> GetRefsAsync(string docId);
    Task<IReadOnlyList<DocRelation>> GetRelationsAsync(string docId, bool asSource);
    Task UpsertRelationAsync(DocRelation relation);
    Task DeleteRelationAsync(string sourceId, string targetId, RelationKind kind);
    Task<CoveragePolicy> GetPolicyAsync(CancellationToken ct = default);
    Task SetPolicyAsync(CoveragePolicy policy, CancellationToken ct = default);
    Task<IReadOnlyList<DocSearchResult>> SearchAsync(
        string query, string? workspaceId = null, DocKind? kind = null, string? topic = null,
        string? pathPrefix = null, int limit = 10);
    Task<IReadOnlyList<DocListItem>> ListAsync(
        string? workspaceId = null, DocKind? kind = null, string? topic = null,
        string sort = "name", string order = "asc", bool includeArchived = false);
}
