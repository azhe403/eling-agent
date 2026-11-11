using Microsoft.Extensions.Logging;

using Eling.Core.Database.Adapters;
using System.Diagnostics;

namespace Eling.Core.Database;

/// <summary>
/// The single read path for database reads. Every caller — the HTTP endpoints
/// and the MCP tools — goes through this service, so the cached fallback
/// behaves identically on both surfaces.
/// </summary>
public sealed class DatabaseReadService : IDatabaseReadService
{
    private readonly IDatabaseReadCache _cache;
    private readonly ILogger<DatabaseReadService>? _logger;

    public DatabaseReadService(
        IDatabaseReadCache cache,
        ILogger<DatabaseReadService>? logger = null)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<QueryResult> QueryAsync(
        DatabaseConnection connection,
        string? database,
        string sql,
        int maxRows,
        CancellationToken cancellationToken = default,
        bool preferCache = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("sql is required", nameof(sql));
        }

        if (maxRows < 1) maxRows = 1;
        if (maxRows > 1000) maxRows = 1000;

        // SQLite is local: a live read is as fast as the cache lookup
        // plus the cache file read, and a snapshot is staler than either.
        // It stays live-first.
        var useCache = preferCache && connection.Provider != DatabaseProvider.Sqlite;
        if (useCache
            && _cache.TryGetQuery(connection.Id, database, maxRows, sql, out var snapshot, out var snapshotAt)
            && snapshot is not null)
        {
            return snapshot with
            {
                FromCache = true,
                CachedAt = snapshotAt
            };
        }

        var target = Target(connection, database);
        var sw = Stopwatch.StartNew();
        try
        {
            var adapter = DatabaseAdapterFactory.CreateAdapter(target);
            var result = await adapter.QueryAsync(sql, maxRows, cancellationToken);
            if (connection.Provider != DatabaseProvider.Sqlite)
            {
                _cache.SetQuery(connection.Id, database, maxRows, sql, result);
            }
            return result with { FromCache = false };
        }
        catch (DatabaseUnavailableException ex)
        {
            sw.Stop();
            if (_cache.TryGetQuery(connection.Id, database, maxRows, sql, out var cached, out var cachedAt)
                && cached is not null)
            {
                _logger?.LogWarning(ex, "Database unavailable; serving cached query result for {Id}", connection.Id);
                return cached with
                {
                    FromCache = true,
                    CachedAt = cachedAt
                };
            }

            throw;
        }
    }

    public async Task<DatabaseSchemaInfo> GetSchemaAsync(
        DatabaseConnection connection,
        string? database,
        CancellationToken cancellationToken = default,
        bool preferCache = true)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var serverLevel = DatabaseConnectionStringFactory.IsServerLevel(connection.Provider, connection.ConnectionString);
        var isServerSchema = serverLevel && string.IsNullOrWhiteSpace(database);

        if (isServerSchema)
        {
            return await ReadDatabaseListAsync(connection, cancellationToken, preferCache);
        }

        // A snapshot on file answers at once, whatever its age: the caller
        // shows the tables and asks again for the real answer. No database
        // round trip on the first paint.
        if (preferCache
            && _cache.TryGetSchema(connection.Id, database, out var cached, out var cachedAt)
            && cached is not null && cachedAt is not null)
        {
            return Decorate(cached, cachedAt.Value, CacheSources.Cached);
        }

        var target = Target(connection, database);
        var adapter = DatabaseAdapterFactory.CreateAdapter(target);
        try
        {
            var schema = await adapter.GetSchemaAsync(database, cancellationToken);
            _cache.SetSchema(connection.Id, database, schema);
            return schema with { Source = CacheSources.Live };
        }
        catch (DatabaseUnavailableException ex)
        {
            if (_cache.TryGetSchema(connection.Id, database, out var stale, out var staleAt)
                && stale is not null && staleAt is not null)
            {
                _logger?.LogWarning(ex, "Database unavailable; serving cached schema for {Id}", connection.Id);
                return Decorate(stale, staleAt.Value, CacheSources.Offline);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken = default,
        bool preferCache = true)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var useCache = preferCache && connection.Provider != DatabaseProvider.Sqlite;
        if (useCache
            && _cache.TryGetDatabaseList(connection.Id, out var cached, out var cachedAt)
            && cached is not null)
        {
            return cached;
        }

        var adapter = DatabaseAdapterFactory.CreateAdapter(connection);
        try
        {
            var databases = await adapter.ListDatabasesAsync(cancellationToken);
            if (connection.Provider != DatabaseProvider.Sqlite)
            {
                _cache.SetDatabaseList(connection.Id, databases);
            }
            return databases;
        }
        catch (DatabaseUnavailableException ex)
        {
            if (_cache.TryGetDatabaseList(connection.Id, out var cachedList, out var cachedListAt)
                && cachedList is not null)
            {
                _logger?.LogWarning(ex, "Database unavailable; serving cached database list for {Id}", connection.Id);
                return cachedList;
            }

            throw;
        }
    }

    /// <summary>
    /// The server-level path: a connection registered without a database. The
    /// database list and the empty table list are one read, so they share one
    /// cache entry and cannot disagree with each other.
    /// </summary>
    private async Task<DatabaseSchemaInfo> ReadDatabaseListAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken,
        bool preferCache)
    {
        if (preferCache
            && _cache.TryGetDatabaseList(connection.Id, out var databases, out var databasesAt)
            && databases is not null && databasesAt is not null)
        {
            var schema = new DatabaseSchemaInfo([]) { Databases = databases };
            return Decorate(schema, databasesAt.Value, CacheSources.Cached);
        }

        try
        {
            return await RefreshDatabaseListAsync(connection, cancellationToken);
        }
        catch (DatabaseUnavailableException ex)
        {
            if (_cache.TryGetDatabaseList(connection.Id, out var cached, out var cachedAt)
                && cached is not null && cachedAt is not null)
            {
                var schema = new DatabaseSchemaInfo([]) { Databases = cached };
                _logger?.LogWarning(ex, "Database unavailable; serving cached database list for {Id}", connection.Id);
                return Decorate(schema, cachedAt.Value, CacheSources.Offline);
            }

            if (_cache.TryGetSchema(connection.Id, null, out var cachedSchema, out var schemaCachedAt)
                && cachedSchema is not null && schemaCachedAt is not null)
            {
                _logger?.LogWarning(ex, "Database unavailable; serving cached schema for {Id}", connection.Id);
                return Decorate(cachedSchema, schemaCachedAt.Value, CacheSources.Offline);
            }

            throw;
        }
    }

    private async Task<DatabaseSchemaInfo> RefreshDatabaseListAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken)
    {
        var adapter = DatabaseAdapterFactory.CreateAdapter(connection);
        var databases = await adapter.ListDatabasesAsync(cancellationToken);
        var schema = new DatabaseSchemaInfo([]) { Databases = databases };
        _cache.SetDatabaseList(connection.Id, databases);
        _cache.SetSchema(connection.Id, null, schema);
        return schema with { Source = CacheSources.Live };
    }

    /// <summary>
    /// The cached snapshot, dressed with the facts a reader needs: where it
    /// came from, when it stops being served, and how long it has left.
    /// </summary>
    private static DatabaseSchemaInfo Decorate(
        DatabaseSchemaInfo cached,
        DateTimeOffset cachedAt,
        string source)
    {
        var expiresAt = cachedAt + IDatabaseReadCache.DefaultMaxAge;
        var ttl = (long)Math.Max(0, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        return cached with
        {
            Source = source,
            CachedAt = cachedAt,
            CacheExpiresAt = expiresAt,
            CacheTtlSeconds = ttl
        };
    }

    private static DatabaseConnection Target(DatabaseConnection connection, string? database)
    {
        if (string.IsNullOrWhiteSpace(database)) return connection;

        return connection with
        {
            ConnectionString = DatabaseConnectionStringFactory.WithDatabase(
                connection.Provider,
                connection.ConnectionString,
                database)
        };
    }

}
