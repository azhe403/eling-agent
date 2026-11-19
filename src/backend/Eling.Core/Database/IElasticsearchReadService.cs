using System.Text.Json;

namespace Eling.Core.Database;

/// <summary>
/// The read-only contract for talking to an Elasticsearch cluster. Read-only is
/// enforced by construction: no member on this interface may create, update,
/// delete or otherwise mutate documents, indices or mappings.
/// </summary>
public interface IElasticsearchReadService
{
    Task<ElasticsearchPingResult> PingAsync(
        DatabaseConnection connection,
        CancellationToken ct);

    Task<IReadOnlyList<ElasticsearchIndexInfo>> ListIndicesAsync(
        DatabaseConnection connection,
        bool includeSystem,
        CancellationToken ct);

    Task<ElasticsearchIndexDetail> GetIndexDetailAsync(
        DatabaseConnection connection,
        string index,
        CancellationToken ct);

    Task<ElasticsearchDocumentPage> DocumentsAsync(
        DatabaseConnection connection,
        string index,
        int from,
        int size,
        string? q,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? field,
        IReadOnlyList<ElasticsearchFilterRow>? filters,
        string? logic,
        CancellationToken ct);

    Task<IReadOnlyList<ElasticsearchHistogramBucket>> HistogramAsync(
        DatabaseConnection connection,
        string index,
        string? q,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? field,
        int buckets,
        IReadOnlyList<ElasticsearchFilterRow>? filters,
        string? logic,
        CancellationToken ct);

    Task<ElasticsearchSearchResult> SearchAsync(
        DatabaseConnection connection,
        string index,
        JsonElement? query,
        JsonElement? sort,
        JsonElement? aggs,
        int size,
        int from,
        IReadOnlyList<string>? sourceIncludes,
        CancellationToken ct);

    Task<long> CountAsync(
        DatabaseConnection connection,
        string index,
        JsonElement? query,
        CancellationToken ct);
}
