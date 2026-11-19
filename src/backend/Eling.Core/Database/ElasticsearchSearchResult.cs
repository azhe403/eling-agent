using System.Text.Json;

namespace Eling.Core.Database;

public sealed record ElasticsearchSearchResult(
    long Total,
    long TookMs,
    IReadOnlyList<ElasticsearchHit> Hits,
    JsonElement? Aggregations);
