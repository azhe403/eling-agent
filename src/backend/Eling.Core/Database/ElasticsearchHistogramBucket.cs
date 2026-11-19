namespace Eling.Core.Database;

public sealed record ElasticsearchHistogramBucket(
    long KeyMs,
    long DocCount);
