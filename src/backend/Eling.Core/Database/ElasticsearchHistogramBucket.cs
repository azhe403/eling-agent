namespace Eling.Core.Database;

public sealed record ElasticsearchHistogramBucket(
    long KeyMs,
    long DocCount)
{
    /// <summary>
    /// The bucket width Elasticsearch chose for the <c>auto_date_histogram</c> (e.g.
    /// <c>1d</c>). Passthrough from the cluster; null only when the cluster returned
    /// no aggregation block. Every bucket of one response carries the same value.
    /// </summary>
    public string? Interval { get; init; }
}
