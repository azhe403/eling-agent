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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var serverLevel = DatabaseConnectionStringFactory.IsServerLevel(connection.Provider, connection.ConnectionString);
        var isServerSchema = serverLevel && string.IsNullOrWhiteSpace(database);

        if (isServerSchema)
        {
            return await ReadDatabaseListAsync(connection, cancellationToken);
        }

        var target = Target(connection, database);
        var adapter = DatabaseAdapterFactory.CreateAdapter(target);
        try
        {
            var schema = await adapter.GetSchemaAsync(database, cancellationToken);
            _cache.SetSchema(connection.Id, database, schema);
            return schema with { FromCache = false };
        }
        catch (DatabaseUnavailableException ex)
        {
            if (_cache.TryGetSchema(connection.Id, database, out var cached, out var cachedAt)
                && cached is not null)
            {
                _logger?.LogWarning(ex, "Database unavailable; serving cached schema for {Id}", connection.Id);
                return cached with
                {
                    FromCache = true,
                    CachedAt = cachedAt
                };
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

    private async Task<DatabaseSchemaInfo> ReadDatabaseListAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken)
    {
        var adapter = DatabaseAdapterFactory.CreateAdapter(connection);
        try
        {
            var databases = await adapter.ListDatabasesAsync(cancellationToken);
            var schema = new DatabaseSchemaInfo([]) { Databases = databases };
            _cache.SetDatabaseList(connection.Id, databases);
            _cache.SetSchema(connection.Id, null, schema);
            return schema with { FromCache = false };
        }
        catch (DatabaseUnavailableException ex)
        {
            if (_cache.TryGetDatabaseList(connection.Id, out var cached, out var cachedAt)
                && cached is not null)
            {
                var schema = new DatabaseSchemaInfo([]) { Databases = cached };
                _logger?.LogWarning(ex, "Database unavailable; serving cached database list for {Id}", connection.Id);
                return schema with
                {
                    FromCache = true,
                    CachedAt = cachedAt
                };
            }

            if (_cache.TryGetSchema(connection.Id, null, out var cachedSchema, out var schemaCachedAt)
                && cachedSchema is not null)
            {
                _logger?.LogWarning(ex, "Database unavailable; serving cached schema for {Id}", connection.Id);
                return cachedSchema with
                {
                    FromCache = true,
                    CachedAt = schemaCachedAt
                };
            }

            throw;
        }
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