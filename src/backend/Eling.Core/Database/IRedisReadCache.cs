namespace Eling.Core.Database;

/// <summary>
/// The offline fallback for Redis reads. Every read that follows the standard
/// cache-aside pattern may be served from here when the server cannot answer:
/// one live attempt is cached verbatim, and an unreachable failure replays the
/// most recent successful answer, marked <c>FromCache</c> with the moment it
/// was taken. That is the database list, a scan page of the key tree, and a
/// single key's TYPE/TTL/length/preview — the small, stable answers a dead
/// server can still be asked about honestly, always with their age visible.
/// Entries are served only while the server is unreachable and only if they
/// are younger than <see cref="MaxAge"/>; a live answer is never replaced by
/// a cached one, and a live refusal (wrong type, bad credentials) is never
/// papered over.
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

    /// <summary>Try to read one cached scan page.</summary>
    bool TryGetPage(
        string connectionId,
        RedisDatabaseIndex database,
        string prefix,
        long cursor,
        int pageOffset,
        int pageSize,
        out KeyPage? value,
        out DateTimeOffset? cachedAt);

    /// <summary>
    /// Store one scan page, refreshing its timestamp. Returns the page as the
    /// cache will serve it, i.e. with keys that have left the keyspace carried
    /// over and flagged <see cref="KeyLeaf.Expired"/>. The live answer is not
    /// what the reader sees: the tree must not blink a row out of existence.
    /// </summary>
    /// <remarks>
    /// The stored page keeps the keys it already had. A key that has left the
    /// keyspace since the last write is not dropped from the entry: it is
    /// carried over marked <see cref="KeyLeaf.Expired"/>, so the tree can keep
    /// showing the row a reader was watching instead of it disappearing on its
    /// own. The merge lives here rather than in the dashboard because this is
    /// the one place that still holds the previous page when the new one
    /// arrives - the client only ever sees the answer.
    /// </remarks>
    KeyPage SetPage(
        string connectionId,
        RedisDatabaseIndex database,
        string prefix,
        long cursor,
        int pageOffset,
        int pageSize,
        KeyPage value);

    /// <summary>Drop every entry for a connection (edited, deleted, key mutated).</summary>
    void InvalidateConnection(string connectionId);
}
