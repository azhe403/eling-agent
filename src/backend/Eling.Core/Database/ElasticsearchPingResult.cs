namespace Eling.Core.Database;

public sealed record ElasticsearchPingResult(
    string Version,
    long LatencyMs);
