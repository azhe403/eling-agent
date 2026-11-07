using System.Collections.Concurrent;
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
    /// <summary>
    /// How long a cached snapshot counts as current. Inside this window the
    /// cache answers without touching the database: a refresh that succeeded
    /// moments ago is the evidence that the snapshot is still true.
    ///
    /// <para>
    /// The value lives on the cache port, not here, because the cache needs
    /// the same number to decide when an unchanged result may keep its old
    /// timestamp. If the two drifted apart, the cache would keep a timestamp
    /// the service already considers stale, and the entry would be rewritten
    /// on every read and never settle.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan FreshWindow = IDatabaseReadCache.FreshWindow;

    /// <summary>
    /// How old a snapshot may get before a read of it triggers a background
    /// refresh.
    ///
    /// <para>
    /// Without this the snapshot's timestamp would only move when the entry
    /// was already stale: a fresh entry is served without touching the
    /// database, so nothing would refresh it, and it would sit at the edge
    /// of the freshness window until it expired. The refresh keeps the
    /// timestamp moving, so the answer stays honestly fresh instead of
    /// flapping between "live" and "refreshing".
    /// </para>
    /// </summary>
    internal static readonly TimeSpan RefreshDue = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the service waits before probing a database that just refused.
    /// Without it every dashboard poll would hammer a dead server.
    /// </summary>
    internal static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    private readonly IDatabaseReadCache _cache;
    private readonly ILogger<DatabaseReadService>? _logger;

    /// <summary>The keys with a refresh in flight, so one refresh runs per key.</summary>
    private readonly ConcurrentDictionary<string, byte> _refreshing = new(StringComparer.Ordinal);

    /// <summary>The keys whose last background refresh failed, and when it failed.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("sql is required", nameof(sql));
        }

        if (maxRows < 1) maxRows = 1;
        if (maxRows > 1000) maxRows = 1000;

        var target = Target(connection, database);

        var adapter = DatabaseAdapterFactory.CreateAdapter(target);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await adapter.QueryAsync(sql, maxRows, cancellationToken);
            _cache.SetQuery(connection.Id, database, maxRows, sql, result);
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

        var key = CacheKey(connection.Id, database);

        if (preferCache)
        {
            var cached = ServeFromCache(
                connection,
                database,
                key,
                ct => RefreshSchemaAsync(connection, database, ct));
            if (cached is not null) return cached;
        }

        var target = Target(connection, database);
        var adapter = DatabaseAdapterFactory.CreateAdapter(target);
        try
        {
            var schema = await adapter.GetSchemaAsync(database, cancellationToken);
            _cache.SetSchema(connection.Id, database, schema);
            _failed.TryRemove(key, out _);
            return schema with { Source = CacheSources.Live };
        }
        catch (DatabaseUnavailableException ex)
        {
            _failed[key] = DateTimeOffset.UtcNow;
            if (_cache.TryGetSchema(connection.Id, database, out var cached, out var cachedAt)
                && cached is not null && cachedAt is not null)
            {
                _logger?.LogWarning(ex, "Database unavailable; serving cached schema for {Id}", connection.Id);
                return Decorate(cached, cachedAt.Value, CacheSources.Offline);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var adapter = DatabaseAdapterFactory.CreateAdapter(connection);
        try
        {
            var databases = await adapter.ListDatabasesAsync(cancellationToken);
            _cache.SetDatabaseList(connection.Id, databases);
            return databases;
        }
        catch (DatabaseUnavailableException ex)
        {
            if (_cache.TryGetDatabaseList(connection.Id, out var cached, out var cachedAt)
                && cached is not null)
            {
                _logger?.LogWarning(ex, "Database unavailable; serving cached database list for {Id}", connection.Id);
                return cached;
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
        var key = CacheKey(connection.Id, null);

        if (preferCache)
        {
            var cached = ServeFromCache(
                connection,
                null,
                key,
                ct => RefreshDatabaseListAsync(connection, ct));
            if (cached is not null) return cached;
        }

        try
        {
            return await RefreshDatabaseListAsync(connection, cancellationToken);
        }
        catch (DatabaseUnavailableException ex)
        {
            _failed[key] = DateTimeOffset.UtcNow;

            if (_cache.TryGetDatabaseList(connection.Id, out var databases, out var databasesAt)
                && databases is not null && databasesAt is not null)
            {
                var schema = new DatabaseSchemaInfo([]) { Databases = databases };
                _logger?.LogWarning(ex, "Database unavailable; serving cached database list for {Id}", connection.Id);
                return Decorate(schema, databasesAt.Value, CacheSources.Offline);
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

    private async Task<DatabaseSchemaInfo> RefreshSchemaAsync(
        DatabaseConnection connection,
        string? database,
        CancellationToken cancellationToken)
    {
        var target = Target(connection, database);
        var adapter = DatabaseAdapterFactory.CreateAdapter(target);
        var schema = await adapter.GetSchemaAsync(database, cancellationToken);
        _cache.SetSchema(connection.Id, database, schema);
        return schema with { Source = CacheSources.Live };
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
    /// The cached answer for a key, served without touching the database when
    /// one exists.
    ///
    /// <para>
    /// A snapshot inside the freshness window is answered as <c>live</c>: a
    /// refresh that succeeded moments ago is the evidence that it is still
    /// true, and the reader should not be told about a cache it does not need
    /// to know about.
    /// </para>
    ///
    /// <para>
    /// An older one is answered as <c>refreshing</c> while a refresh goes out
    /// to replace it — or as <c>offline</c> when the last refresh failed and
    /// the backoff has not passed yet, so a dead server is probed once per
    /// backoff window rather than once per dashboard poll.
    /// </para>
    /// </summary>
    private DatabaseSchemaInfo? ServeFromCache(
        DatabaseConnection connection,
        string? database,
        string key,
        Func<CancellationToken, Task<DatabaseSchemaInfo>> refresh)
    {
        if (!_cache.TryGetSchema(connection.Id, database, out var cached, out var cachedAt)
            || cached is null || cachedAt is null)
        {
            return null;
        }

        var age = DateTimeOffset.UtcNow - cachedAt.Value;

        if (age < FreshWindow)
        {
            // Still current. But a snapshot that nobody refreshes keeps an
            // old timestamp and eventually reads as stale, so one that has
            // been sitting a while gets a refresh on the way out - the answer
            // stays current without the caller waiting for the database.
            if (age >= RefreshDue)
            {
                FireBackgroundRefresh(key, refresh);
            }

            return Decorate(cached, cachedAt.Value, CacheSources.Live);
        }

        if (IsCoolingDown(key))
        {
            return Decorate(cached, cachedAt.Value, CacheSources.Offline);
        }

        FireBackgroundRefresh(key, refresh);
        return Decorate(cached, cachedAt.Value, CacheSources.Refreshing);
    }

    /// <summary>
    /// Whether this key failed to refresh recently enough that probing it again
    /// would be pointless.
    /// </summary>
    private bool IsCoolingDown(string key)
        => _failed.TryGetValue(key, out var at) && DateTimeOffset.UtcNow - at < RetryBackoff;

    /// <summary>
    /// Fire a refresh in the background and return at once. One refresh per key
    /// runs at a time: a dashboard that polls every five seconds must not start
    /// a second one while the first is still talking to a slow server.
    ///
    /// <para>
    /// The refresh outlives the request it was fired from, so it runs with no
    /// cancellation token and on a thread the request does not wait for. A
    /// failure is recorded rather than thrown: the next read is the one that
    /// reports the database as offline.
    /// </para>
    /// </summary>
    private void FireBackgroundRefresh(string key, Func<CancellationToken, Task<DatabaseSchemaInfo>> refresh)
    {
        if (!_refreshing.TryAdd(key, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await refresh(CancellationToken.None);
                _failed.TryRemove(key, out _);
            }
            catch (DatabaseUnavailableException ex)
            {
                _failed[key] = DateTimeOffset.UtcNow;
                _logger?.LogWarning(ex, "Background refresh failed for {Key}", key);
            }
            catch (Exception ex)
            {
                _failed[key] = DateTimeOffset.UtcNow;
                _logger?.LogWarning(ex, "Background refresh failed for {Key}", key);
            }
            finally
            {
                _refreshing.TryRemove(key, out _);
            }
        });
    }

    /// <summary>
    /// The cached snapshot, dressed with the three facts a reader needs: where
    /// it came from, when it stops being served, and how long it has left.
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

    private static string CacheKey(string connectionId, string? database)
        => $"{connectionId}|{database ?? string.Empty}";

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
