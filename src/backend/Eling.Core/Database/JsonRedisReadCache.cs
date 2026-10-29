using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Eling.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Database;

/// <summary>
/// File-backed <see cref="IRedisReadCache"/>. One file, written the way the SQL
/// cache writes its own: under a named mutex, to a temp file, then renamed over
/// the real one — the mechanics live in <see cref="CacheFileGate"/>.
/// </summary>
public sealed class JsonRedisReadCache : IRedisReadCache
{
    private const int MaxKeysPerConnection = 500;

    private readonly CacheFileGate _gate;
    private readonly ILogger<JsonRedisReadCache>? _logger;

    /// <summary>What an empty cache file looks like.</summary>
    private static RedisCacheFile Empty() => new([], []);

    public JsonRedisReadCache(string dataDirectory, ILogger<JsonRedisReadCache>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var dir = Path.GetFullPath(dataDirectory);
        _logger = logger;
        // Its own lock name: the Redis cache shares the directory with the SQL
        // cache but has nothing to do with its files.
        _gate = new CacheFileGate(
            Path.Combine(dir, "database-redis-cache.json"),
            CacheFileLock.NameFor(dir, "redis"),
            logger);
    }

    public bool TryGetDatabases(
        string connectionId,
        out IReadOnlyList<RedisDatabaseInfo>? value,
        out DateTimeOffset? cachedAt)
    {
        value = null;
        cachedAt = null;
        if (string.IsNullOrWhiteSpace(connectionId)) return false;

        return Read(connectionId, out value, out cachedAt, file =>
        {
            var entry = file.Databases.FirstOrDefault(e =>
                string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal));

            return entry is null || IsExpired(entry.CachedAt)
                ? null
                : new CachedEntry<IReadOnlyList<RedisDatabaseInfo>>(entry.Databases, entry.CachedAt);
        });
    }

    public void SetDatabases(string connectionId, IReadOnlyList<RedisDatabaseInfo> value)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;

        Write(connectionId, file =>
        {
            var entry = new CachedRedisDatabasesEntry(connectionId, DateTimeOffset.UtcNow, value);

            var idx = file.Databases.FindIndex(e =>
                string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal));

            if (idx >= 0) file.Databases[idx] = entry;
            else file.Databases.Add(entry);
        });
    }

    public bool TryGetKey(
        string connectionId,
        RedisDatabaseIndex database,
        string key,
        out KeyInspectResult? value,
        out DateTimeOffset? cachedAt)
    {
        value = null;
        cachedAt = null;
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrEmpty(key)) return false;

        var hash = HashKey(key);
        return Read(connectionId, out value, out cachedAt, file =>
        {
            var entry = file.Keys.FirstOrDefault(e =>
                string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal) &&
                e.Database == database.Value &&
                string.Equals(e.KeyHash, hash, StringComparison.Ordinal));

            return entry is null || IsExpired(entry.CachedAt)
                ? null
                : new CachedEntry<KeyInspectResult>(entry.Result, entry.CachedAt);
        });
    }

    public void SetKey(string connectionId, RedisDatabaseIndex database, KeyInspectResult value)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;

        Write(connectionId, file =>
        {
            var entry = new CachedRedisKeyEntry(
                connectionId, database.Value, HashKey(value.Key), DateTimeOffset.UtcNow, value);

            var idx = file.Keys.FindIndex(e =>
                string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal) &&
                e.Database == database.Value &&
                string.Equals(e.KeyHash, entry.KeyHash, StringComparison.Ordinal));

            if (idx >= 0) file.Keys[idx] = entry;
            else file.Keys.Add(entry);

            CapKeys(file, connectionId);
        });
    }

    public void RemoveKey(string connectionId, RedisDatabaseIndex database, string key)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrEmpty(key)) return;

        var hash = HashKey(key);
        try
        {
            _gate.WithGate(() =>
            {
                var file = _gate.Load(Empty);
                var removed = file.Keys.RemoveAll(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal) &&
                    e.Database == database.Value &&
                    string.Equals(e.KeyHash, hash, StringComparison.Ordinal));

                if (removed > 0) _gate.Save(file);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to drop cached Redis key for {Id}.", connectionId);
        }
    }

    public void InvalidateConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;

        try
        {
            _gate.WithGate(() =>
            {
                var file = _gate.Load(Empty);
                var removed = file.Databases.RemoveAll(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal));
                removed += file.Keys.RemoveAll(e =>
                    string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal));

                if (removed > 0) _gate.Save(file);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to invalidate cached Redis entries for {Id}.", connectionId);
        }
    }

    /// <summary>
    /// Shared read shape: take the gate, let the caller pick the entry out of
    /// the file, then hand the timestamp back with the value.
    /// </summary>
    private bool Read<T>(
        string connectionId,
        out T? value,
        out DateTimeOffset? cachedAt,
        Func<RedisCacheFile, CachedEntry<T>?> pick)
    {
        value = default;
        cachedAt = null;

        try
        {
            T? found = default;
            DateTimeOffset? at = null;
            var hit = false;

            _gate.WithGate(() =>
            {
                var picked = pick(_gate.Load(Empty));
                if (picked is null) return;
                found = picked.Value;
                at = picked.CachedAt;
                hit = true;
            });

            if (hit)
            {
                value = found;
                cachedAt = at;
            }
            return hit;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read cached Redis entry for {Id} from {Path}.", connectionId, _gate.Path);
            return false;
        }
    }

    private void Write(string connectionId, Action<RedisCacheFile> mutate)
    {
        try
        {
            _gate.WithGate(() =>
            {
                var file = _gate.Load(Empty);
                mutate(file);
                _gate.Save(file);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to write cached Redis entry for {Id} to {Path}.", connectionId, _gate.Path);
        }
    }

    /// <summary>
    /// One connection can be inspected across hundreds of keys; keep the newest
    /// so the file cannot grow without bound.
    /// </summary>
    private static void CapKeys(RedisCacheFile file, string connectionId)
    {
        var mine = file.Keys
            .Where(e => string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal))
            .ToList();

        if (mine.Count <= MaxKeysPerConnection) return;

        var toDrop = mine
            .OrderBy(e => e.CachedAt)
            .Take(mine.Count - MaxKeysPerConnection)
            .ToHashSet();

        file.Keys.RemoveAll(e => toDrop.Contains(e));
    }

    private static bool IsExpired(DateTimeOffset cachedAt) =>
        DateTimeOffset.UtcNow - cachedAt > IRedisReadCache.MaxAge;

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }


    /// <summary>
    /// One entry found in the file: the value, and when it was recorded.
    /// </summary>
    private sealed record CachedEntry<T>(
        T Value,
        DateTimeOffset CachedAt);

    private sealed record RedisCacheFile(
        List<CachedRedisDatabasesEntry> Databases,
        List<CachedRedisKeyEntry> Keys);

    private sealed record CachedRedisDatabasesEntry(
        string ConnectionId,
        DateTimeOffset CachedAt,
        IReadOnlyList<RedisDatabaseInfo> Databases);

    /// <summary>
    /// The key itself is hashed for lookup but kept inside the result, so the
    /// cached answer still names the key it belongs to.
    /// </summary>
    private sealed record CachedRedisKeyEntry(
        string ConnectionId,
        int Database,
        string KeyHash,
        DateTimeOffset CachedAt,
        KeyInspectResult Result);
}
