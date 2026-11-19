namespace Eling.Core.Database;

public sealed record ElasticsearchIndexInfo(
    string Name,
    string Health,
    string Status,
    long DocsCount,
    long StoreSizeBytes,
    int PrimaryShards,
    int Replicas);
