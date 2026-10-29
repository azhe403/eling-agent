namespace Eling.Core.Database;

/// <summary>
/// The offline fallback for Redis reads. The keyspace itself is never cached
/// (a stale tree is a lie); what is cached is the small, stable set of answers
/// that a dead server can still be asked about honestly: the list of databases
/// and a single key's TYPE/TTL/length/preview.
///
/// <para>
/// Every key is built from the connection id, never from the connection
/// string, so the cache file holds no credentials. Entries are served only
/// while the server is unreachable, and only if they are younger than
/// <see cref="MaxAge"/>.
/// </para>
/// </summary>
public interface IRedisReadCache
{
    /// <summary>
    /// How long an entry may be served. Shorter than the SQL cache's week:
    /// a key that expired an hour ago is normal, a key that expired a week
    /// ago tells the reader nothing useful.
    /// </summary>
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Try to read the cached database list for a connection.</summary>
    bool TryGetDatabases(
        string connectionId,
        out IReadOnlyList<RedisDatabaseInfo>? value,
        out DateTimeOffset? cachedAt);

    /// <summary>Store the database list, refreshing its timestamp.</summary>
    void SetDatabases(string connectionId, IReadOnlyList<RedisDatabaseInfo> value);

    /// <summary>Try to read one cached key inspection.</summary>
    bool TryGetKey(
        string connectionId,
        RedisDatabaseIndex database,
        string key,
        out KeyInspectResult? value,
        out DateTimeOffset? cachedAt);

    /// <summary>Store one key inspection, refreshing its timestamp.</summary>
    void SetKey(string connectionId, RedisDatabaseIndex database, KeyInspectResult value);

    /// <summary>Drop one key's entry (it was deleted, so the cached answer is stale).</summary>
    void RemoveKey(string connectionId, RedisDatabaseIndex database, string key);

    /// <summary>Drop every entry for a connection (edited, deleted, key mutated).</summary>
    void InvalidateConnection(string connectionId);
}
