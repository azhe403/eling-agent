using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Eling.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Database;

/// <summary>
/// File-backed implementation of <see cref="IDatabaseReadCache"/>. Schema and
/// query (data) entries are stored in two separate files so a data write never
/// rewrites the (rarely changing) schema file and vice versa.
/// </summary>
public sealed class JsonDatabaseReadCache : IDatabaseReadCache, IDisposable
{
    private const string MutexName = @"Global\eling-db-read-cache";
    private const string TempSuffix = ".tmp";

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    private static readonly Mutex Gate = new(initiallyOwned: false, MutexName);

    private readonly string _schemaFilePath;
    private readonly string _queryFilePath;
    private readonly ILogger<JsonDatabaseReadCache>? _logger;

    public JsonDatabaseReadCache(
        string dataDirectory,
        ILogger<JsonDatabaseReadCache>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var dir = Path.GetFullPath(dataDirectory);
        _schemaFilePath = Path.Combine(dir, "database-schema-cache.json");
        _queryFilePath = Path.Combine(dir, "database-query-cache.json");
        _logger = logger;
    }

    public bool TryGetSchema(
        string connectionId,
        string? database,
        out DatabaseSchemaInfo? value,
        out DateTimeOffset? cachedAt)
    {
        value = null;
        cachedAt = null;

        try
        {
            var found = false;
            DatabaseSchemaInfo? v = null;
            DateTimeOffset? ca = null;
            WithGate(() =>
            {
                var file = LoadSchemaCore();
                var entry = file.Schemas.FirstOrDefault(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal) &&
                    string.Equals(e.Database, database, StringComparison.Ordinal));

                if (entry is null) return;
                if (IsExpired(entry.CachedAt)) return;

                v = entry.Schema with
                {
                    FromCache = true,
                    CachedAt = entry.CachedAt
                };
                ca = entry.CachedAt;
                found = true;
            });

            if (found)
            {
                value = v;
                cachedAt = ca;
            }
            return found;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read cached schema for {Id} from {Path}.", connectionId, _schemaFilePath);
            return false;
        }
    }

    public void SetSchema(
        string connectionId,
        string? database,
        DatabaseSchemaInfo value)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;

        try
        {
            WithGate(() =>
            {
                var file = LoadSchemaCore();
                var cachedAt = DateTimeOffset.UtcNow;
                var entry = new CachedSchemaEntry(connectionId, database, cachedAt, value);

                var idx = file.Schemas.FindIndex(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal) &&
                    string.Equals(e.Database, database, StringComparison.Ordinal));

                if (idx >= 0)
                {
                    if (EntriesEqual(file.Schemas[idx], entry)) return;
                    file.Schemas[idx] = entry;
                }
                else
                {
                    file.Schemas.Add(entry);
                }

                PruneSchema(file);
                SaveSchemaCore(file);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to write cached schema for {Id} to {Path}.", connectionId, _schemaFilePath);
        }
    }

    public bool TryGetQuery(
        string connectionId,
        string? database,
        int maxRows,
        string sql,
        out QueryResult? value,
        out DateTimeOffset? cachedAt)
    {
        value = null;
        cachedAt = null;

        if (string.IsNullOrWhiteSpace(sql)) return false;

        try
        {
            var found = false;
            QueryResult? v = null;
            DateTimeOffset? ca = null;
            WithGate(() =>
            {
                var file = LoadQueryCore();
                var hash = HashSql(sql);
                var entry = file.Queries.FirstOrDefault(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal) &&
                    string.Equals(e.Database, database, StringComparison.Ordinal) &&
                    e.MaxRows == maxRows &&
                    string.Equals(e.SqlHash, hash, StringComparison.Ordinal));

                if (entry is null) return;
                if (IsExpired(entry.CachedAt)) return;

                var rows = entry.Rows
                    .Select(row => (IReadOnlyList<object?>)row.Select(JsonCell.ToClrValue).ToList())
                    .ToList();

                v = new QueryResult(entry.Columns, rows, entry.Truncated)
                {
                    FromCache = true,
                    CachedAt = entry.CachedAt
                };
                ca = entry.CachedAt;
                found = true;
            });

            if (found)
            {
                value = v;
                cachedAt = ca;
            }
            return found;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read cached query for {Id} from {Path}.", connectionId, _queryFilePath);
            return false;
        }
    }

    public void SetQuery(
        string connectionId,
        string? database,
        int maxRows,
        string sql,
        QueryResult value)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(sql)) return;

        try
        {
            WithGate(() =>
            {
                var file = LoadQueryCore();
                var cachedAt = DateTimeOffset.UtcNow;
                var rows = value.Rows
                    .Select(row => (IReadOnlyList<JsonElement>)row
                        .Select(ToJsonElement).ToList())
                    .ToList();

                var entry = new CachedQueryEntry(
                    connectionId,
                    database,
                    maxRows,
                    HashSql(sql),
                    cachedAt,
                    value.Columns,
                    rows,
                    value.Truncated);

                var idx = file.Queries.FindIndex(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal) &&
                    string.Equals(e.Database, database, StringComparison.Ordinal) &&
                    e.MaxRows == maxRows &&
                    string.Equals(e.SqlHash, entry.SqlHash, StringComparison.Ordinal));

                if (idx >= 0)
                {
                    if (EntriesEqual(file.Queries[idx], entry)) return;
                    file.Queries[idx] = entry;
                }
                else
                {
                    file.Queries.Add(entry);
                    CapQueryEntries(file, connectionId);
                }

                PruneQuery(file);
                SaveQueryCore(file);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to write cached query for {Id} to {Path}.", connectionId, _queryFilePath);
        }
    }

    public bool TryGetDatabaseList(
        string connectionId,
        out IReadOnlyList<string>? value,
        out DateTimeOffset? cachedAt)
    {
        value = null;
        cachedAt = null;

        try
        {
            var found = false;
            IReadOnlyList<string>? v = null;
            DateTimeOffset? ca = null;
            WithGate(() =>
            {
                var file = LoadSchemaCore();
                var entry = file.DatabaseLists.FirstOrDefault(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal));

                if (entry is null) return;
                if (IsExpired(entry.CachedAt)) return;

                v = entry.Databases;
                ca = entry.CachedAt;
                found = true;
            });

            if (found)
            {
                value = v;
                cachedAt = ca;
            }
            return found;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read cached database list for {Id} from {Path}.", connectionId, _schemaFilePath);
            return false;
        }
    }

    public void SetDatabaseList(
        string connectionId,
        IReadOnlyList<string> value)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;

        try
        {
            WithGate(() =>
            {
                var file = LoadSchemaCore();
                var cachedAt = DateTimeOffset.UtcNow;
                var entry = new CachedDatabaseListEntry(connectionId, cachedAt, value);

                var idx = file.DatabaseLists.FindIndex(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal));

                if (idx >= 0)
                {
                    if (EntriesEqual(file.DatabaseLists[idx], entry)) return;
                    file.DatabaseLists[idx] = entry;
                }
                else
                {
                    file.DatabaseLists.Add(entry);
                }

                PruneSchema(file);
                SaveSchemaCore(file);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to write cached database list for {Id} to {Path}.", connectionId, _schemaFilePath);
        }
    }

    public void InvalidateConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;

        try
        {
            WithGate(() =>
            {
                // Schema file
                var schemaFile = LoadSchemaCore();
                var sChanged = false;
                if (schemaFile.Schemas.RemoveAll(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal)) > 0) sChanged = true;
                if (schemaFile.DatabaseLists.RemoveAll(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal)) > 0) sChanged = true;
                if (sChanged) SaveSchemaCore(schemaFile);

                // Query file
                var queryFile = LoadQueryCore();
                if (queryFile.Queries.RemoveAll(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal)) > 0)
                {
                    SaveQueryCore(queryFile);
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to invalidate cached entries for {Id}.", connectionId);
        }
    }

    public void Dispose()
    {
    }

    private static bool IsExpired(DateTimeOffset cachedAt)
    {
        return DateTimeOffset.UtcNow - cachedAt > IDatabaseReadCache.DefaultMaxAge;
    }

    private static string HashSql(string sql)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static JsonElement ToJsonElement(object? value)
    {
        if (value is JsonElement je) return je;
        return JsonSerializer.SerializeToElement(value, JsonDefaults.Shared);
    }

    private static void CapQueryEntries(DatabaseQueryCacheFile file, string connectionId)
    {
        const int Cap = 200;
        var mine = file.Queries
            .Where(e => string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal))
            .ToList();

        if (mine.Count <= Cap) return;

        var toDrop = mine
            .OrderBy(e => e.CachedAt)
            .Take(mine.Count - Cap)
            .ToHashSet();

        file.Queries.RemoveAll(e => toDrop.Contains(e));
    }

    private static void PruneSchema(DatabaseSchemaCacheFile file)
    {
        file.Schemas.RemoveAll(e => IsExpired(e.CachedAt));
        file.DatabaseLists.RemoveAll(e => IsExpired(e.CachedAt));
    }

    private static void PruneQuery(DatabaseQueryCacheFile file)
    {
        file.Queries.RemoveAll(e => IsExpired(e.CachedAt));
    }

    private DatabaseSchemaCacheFile LoadSchemaCore()
    {
        if (!File.Exists(_schemaFilePath)) return new DatabaseSchemaCacheFile([], []);

        try
        {
            var json = File.ReadAllText(_schemaFilePath);
            if (string.IsNullOrWhiteSpace(json)) return new DatabaseSchemaCacheFile([], []);

            return JsonSerializer.Deserialize<DatabaseSchemaCacheFile>(json, JsonDefaults.Shared)
                ?? new DatabaseSchemaCacheFile([], []);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Ignoring unreadable database schema cache at {Path}.", _schemaFilePath);
            return new DatabaseSchemaCacheFile([], []);
        }
    }

    private void SaveSchemaCore(DatabaseSchemaCacheFile file)
    {
        var dir = Path.GetDirectoryName(_schemaFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _schemaFilePath + TempSuffix;
        var json = JsonSerializer.Serialize(file, JsonDefaults.Shared);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _schemaFilePath, overwrite: true);
    }

    private DatabaseQueryCacheFile LoadQueryCore()
    {
        if (!File.Exists(_queryFilePath)) return new DatabaseQueryCacheFile([]);

        try
        {
            var json = File.ReadAllText(_queryFilePath);
            if (string.IsNullOrWhiteSpace(json)) return new DatabaseQueryCacheFile([]);

            return JsonSerializer.Deserialize<DatabaseQueryCacheFile>(json, JsonDefaults.Shared)
                ?? new DatabaseQueryCacheFile([]);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Ignoring unreadable database query cache at {Path}.", _queryFilePath);
            return new DatabaseQueryCacheFile([]);
        }
    }

    private void SaveQueryCore(DatabaseQueryCacheFile file)
    {
        var dir = Path.GetDirectoryName(_queryFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _queryFilePath + TempSuffix;
        var json = JsonSerializer.Serialize(file, JsonDefaults.Shared);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _queryFilePath, overwrite: true);
    }

    private T WithGate<T>(Func<T> action)
    {
        EnterGate();
        try
        {
            return action();
        }
        finally
        {
            Gate.ReleaseMutex();
        }
    }

    private void WithGate(Action action)
    {
        EnterGate();
        try
        {
            action();
        }
        finally
        {
            Gate.ReleaseMutex();
        }
    }

    private static void EnterGate()
    {
        try
        {
            if (!Gate.WaitOne(GateTimeout))
            {
                throw new TimeoutException(
                    $"Timed out after {GateTimeout} waiting for the database read cache gate.");
            }
        }
        catch (AbandonedMutexException)
        {
        }
    }

    private static bool EntriesEqual(CachedSchemaEntry a, CachedSchemaEntry b)
    {
        return a.Database == b.Database &&
               JsonSerializer.Serialize(a.Schema, JsonDefaults.Shared) ==
               JsonSerializer.Serialize(b.Schema, JsonDefaults.Shared);
    }

    private static bool EntriesEqual(CachedQueryEntry a, CachedQueryEntry b)
    {
        if (a.Columns.Count != b.Columns.Count) return false;
        if (a.Truncated != b.Truncated) return false;
        if (a.Rows.Count != b.Rows.Count) return false;
        if (!a.Columns.SequenceEqual(b.Columns)) return false;
        return JsonSerializer.Serialize(a.Rows, JsonDefaults.Shared) ==
               JsonSerializer.Serialize(b.Rows, JsonDefaults.Shared);
    }

    private static bool EntriesEqual(CachedDatabaseListEntry a, CachedDatabaseListEntry b)
    {
        return a.Databases.SequenceEqual(b.Databases);
    }
}