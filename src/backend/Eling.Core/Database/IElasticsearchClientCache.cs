using Elastic.Clients.Elasticsearch;

namespace Eling.Core.Database;

/// <summary>
/// The pooled, connection-string-aware client cache for Elasticsearch.
///
/// The cache is keyed by <see cref="DatabaseConnection.Id"/> and stores the
/// connection string per entry; a changed string rebuilds the client. Bounded to
/// 8 entries (LRU eviction) and an idle entry older than five minutes is evicted
/// by one <see cref="System.Threading.Timer"/>, like <see cref="IRedisConnectionPool"/>.
/// <see cref="Invalidate"/> drops an entry; it is called from the PUT/DELETE of the
/// connection (Task 8). Nothing about the cluster is cached — only the client.
/// </summary>
public interface IElasticsearchClientCache
{
    /// <summary>
    /// The client for <paramref name="connection"/>, reusing a cached one when the
    /// stored connection string is unchanged.
    /// </summary>
    ElasticsearchClient GetOrCreate(DatabaseConnection connection);

    /// <summary>Drop the entry for <paramref name="connectionId"/>, if any.</summary>
    void Invalidate(string connectionId);
}
