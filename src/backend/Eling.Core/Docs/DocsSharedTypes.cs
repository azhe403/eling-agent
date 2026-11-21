namespace Eling.Core.Docs;

/// <summary>
/// Shared types between DocsService and SqliteDocsIndex.
/// </summary>
public enum OfferState
{
    Unasked = 0,
    Accepted = 1,
    Declined = 2,
    Never = 3
}

/// <summary>
/// Audit entry for document mutations.
/// </summary>
public sealed record DocAuditEntry(
    string Id,
    string DocId,
    string Actor,
    string Action,
    string? BeforeJson,
    string? AfterJson,
    DateTimeOffset At);

/// <summary>
/// Coverage policy for document scanning.
/// </summary>
public sealed record CoveragePolicy
{
    public string[] Include { get; set; } = Array.Empty<string>();
    public string[] Exclude { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Result of a scan operation.
/// </summary>
public sealed record ScanResult(
    int Indexed,
    int Skipped,
    int Deleted,
    int Errors,
    int TotalScanned,
    bool AlreadyRunning = false,
    IReadOnlyList<string>? ErrorMessages = null)
{
    public ScanResult(int indexed, int skipped, int deleted, int errors, int totalScanned, IReadOnlyList<string>? errorMessages = null)
        : this(indexed, skipped, deleted, errors, totalScanned, false, errorMessages)
    {
    }
}

/// <summary>
/// Staleness finding.
/// </summary>
public sealed record StalenessFinding(
    string DocId,
    string Title,
    string RelPath,
    StalenessKind Kind,
    string Message,
    string? Anchor);

/// <summary>
/// Kind of staleness finding.
/// </summary>
public enum StalenessKind
{
    UnresolvedReference,
    SupersededWithoutReplacement,
    StaleDraft,
    Untouched
}

/// <summary>
/// Staleness report.
/// </summary>
public sealed record StalenessReport(
    IReadOnlyList<StalenessFinding> Findings,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Offer check result.
/// </summary>
public sealed record OfferCheckResult(
    bool ShouldOffer,
    OfferState State,
    string Reason);

/// <summary>
/// Rebuild item for index rebuilding.
/// </summary>
public sealed record DocRebuildItem(Doc Doc, IReadOnlyList<DocSection> Sections, IReadOnlyList<DocRef> Refs);

/// <summary>
/// Policy entry.
/// </summary>
public sealed record DocPolicy(string Key, string Value, DateTimeOffset UpdatedAt);

/// <summary>
/// Offer state per project.
/// </summary>
public sealed record DocOfferState(string ProjectHash, OfferState State, DateTimeOffset? AskedAt, DateTimeOffset? AnsweredAt, int SpecsSinceAsk);

/// <summary>
/// Search result.
/// </summary>
public sealed record DocSearchResult(string DocId, DocOwnership Ownership, DocOrigin Origin, string RelPath, DocKind Kind, string Title, int Status, string Topic, int StepsTotal, int StepsDone, double Rank);

/// <summary>
/// List item.
/// </summary>
public sealed record DocListItem(string DocId, DocOwnership Ownership, DocOrigin Origin, string RelPath, DocKind Kind, string Title, int Status, string Topic, int Order, int StepsTotal, int StepsDone);

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