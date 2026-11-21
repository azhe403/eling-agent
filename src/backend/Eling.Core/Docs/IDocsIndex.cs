namespace Eling.Core.Docs;

/// <summary>
/// Interface for docs index to allow testing/mocking.
/// </summary>
public interface IDocsIndex
{
    Task IndexAsync(Doc doc, IReadOnlyList<DocSection> sections, IReadOnlyList<DocRef> refs);
    Task DeleteAsync(string docId);
    Task RebuildAsync(IEnumerable<DocRebuildItem> items);
    Task<Doc?> GetAsync(string docId);
    Task<DocSection?> GetSectionAsync(string docId, string anchor);
    Task<IReadOnlyList<DocSection>> GetSectionsAsync(string docId);
    Task<IReadOnlyList<DocRef>> GetRefsAsync(string docId);
    Task<IReadOnlyList<DocRelation>> GetRelationsAsync(string docId, bool asSource);
    Task UpsertRelationAsync(DocRelation relation);
    Task DeleteRelationAsync(string sourceId, string targetId, RelationKind kind);
    Task WriteAuditAsync(DocAuditEntry entry);
    Task<IReadOnlyList<DocAuditEntry>> GetAuditAsync(string? docId = null, int limit = 100);
    Task<DocPolicy?> GetPolicyAsync(string key);
    Task SetPolicyAsync(DocPolicy policy);
    Task<DocOfferState?> GetOfferStateAsync(string projectHash);
    Task SetOfferStateAsync(DocOfferState state);
    Task<IReadOnlyList<DocSearchResult>> SearchAsync(string query, DocOwnership? ownership = null, DocOrigin? origin = null, DocKind? kind = null, string? topic = null, string? pathPrefix = null, int limit = 10);
    Task<IReadOnlyList<DocListItem>> ListAsync(DocOwnership? ownership = null, DocOrigin? origin = null, DocKind? kind = null, string? topic = null, string sort = "topic");
}