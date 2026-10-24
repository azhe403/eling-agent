using System.Text.Json;

namespace Eling.Core.Database;

/// <summary>
/// A read-through cache for database reads. All keys are built from the
/// connection id (never from the connection string), so the cache file never
/// holds credentials.
/// </summary>
public interface IDatabaseReadCache
{
    /// <summary>Maximum age of a cached entry. Older entries are not served.</summary>
    static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

    /// <summary>Try to read a cached schema entry.</summary>
    bool TryGetSchema(
        string connectionId,
        string? database,
        out DatabaseSchemaInfo? value,
        out DateTimeOffset? cachedAt);

    /// <summary>Store a schema entry, refreshing its timestamp.</summary>
    void SetSchema(
        string connectionId,
        string? database,
        DatabaseSchemaInfo value);

    /// <summary>Try to read a cached query result.</summary>
    bool TryGetQuery(
        string connectionId,
        string? database,
        int maxRows,
        string sql,
        out QueryResult? value,
        out DateTimeOffset? cachedAt);

    /// <summary>Store a query result, refreshing its timestamp.</summary>
    void SetQuery(
        string connectionId,
        string? database,
        int maxRows,
        string sql,
        QueryResult value);

    /// <summary>Try to read a cached database-name list (server-level connection).</summary>
    bool TryGetDatabaseList(
        string connectionId,
        out IReadOnlyList<string>? value,
        out DateTimeOffset? cachedAt);

    /// <summary>Store a database-name list, refreshing its timestamp.</summary>
    void SetDatabaseList(
        string connectionId,
        IReadOnlyList<string> value);

    /// <summary>Drop every cached entry that belongs to one connection.</summary>
    void InvalidateConnection(string connectionId);
}

// Cache entry shapes live in DatabaseReadCacheEntries.cs.