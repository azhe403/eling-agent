using System.Text.Json;

namespace Eling.Backend.Dtos;

/// <summary>
/// Request body for POST /{id}/es/search: the read-only DSL console.
/// <c>size</c> is clamped to 1-100, <c>from</c> must be &gt;= 0 and
/// <c>from + size</c> must be &lt;= 10,000 (ES <c>max_result_window</c>).
/// </summary>
public sealed record ElasticsearchSearchRequest(
    string? Index,
    JsonElement? Query,
    JsonElement? Sort,
    JsonElement? Aggs,
    int Size = 25,
    int From = 0,
    IReadOnlyList<string>? SourceIncludes = null);

/// <summary>
/// Request body for POST /{id}/es/count: the read-only DSL console count.
/// A missing query means <c>match_all</c>.
/// </summary>
public sealed record ElasticsearchCountRequest(
    string? Index,
    JsonElement? Query);
