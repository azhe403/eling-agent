using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Eling.Core.Database;

public sealed class RedisConnectionPool : IRedisConnectionPool, IDisposable
{
    private const int MaxConnections = 16;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public required string ConnectionString { get; init; }
        public required IConnectionMultiplexer Multiplexer { get; init; }
        public DateTimeOffset LastUsed { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Timer _evictor;
    private bool _disposed;

    /// <param name="evictionInterval">
    /// How often the idle sweep runs. The default is one minute, which is
    /// often enough to keep a five-minute idle window honest; tests pass a
    /// long or disabled interval so a sweep never races their assertions.
    /// </param>
    public RedisConnectionPool(TimeSpan? evictionInterval = null)
    {
        var interval = evictionInterval ?? TimeSpan.FromMinutes(1);
        _evictor = new Timer(_ => EvictIdle(), null, interval, interval);
    }

    public async Task<IConnectionMultiplexer> GetAsync(DatabaseConnection connection, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_entries.TryGetValue(connection.Id, out var existing))
        {
            if (existing.ConnectionString == connection.ConnectionString && existing.Multiplexer.IsConnected)
            {
                existing.LastUsed = DateTimeOffset.UtcNow;
                return existing.Multiplexer;
            }

            // Connection string changed or multiplexer dead: rebuild.
            try { await existing.Multiplexer.DisposeAsync(); } catch { }
            _entries.TryRemove(connection.Id, out _);
        }

        // Enforce cap by evicting LRU.
        if (_entries.Count >= MaxConnections)
        {
            var lastUsed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var pair in _entries) lastUsed[pair.Key] = pair.Value.LastUsed;
            var lruKey = RedisPoolPolicy.PickLeastRecentlyUsed(lastUsed);
            if (lruKey is not null && _entries.TryRemove(lruKey, out var removed))
            {
                try { await removed.Multiplexer.DisposeAsync(); } catch { }
            }
        }

        var options = ConfigurationOptions.Parse(connection.ConnectionString);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 5000;
        options.SyncTimeout = 5000;
        options.AsyncTimeout = 5000;
        options.ClientName = "eling-dashboard";
        options.AllowAdmin = false;

        var mux = await ConnectionMultiplexer.ConnectAsync(options);
        var entry = new Entry
        {
            ConnectionString = connection.ConnectionString,
            Multiplexer = mux,
            LastUsed = DateTimeOffset.UtcNow
        };
        _entries[connection.Id] = entry;
        return mux;
    }

    public void Invalidate(string connectionId)
    {
        if (_entries.TryRemove(connectionId, out var removed))
        {
            try { removed.Multiplexer.Dispose(); } catch { }
        }
    }

    private void EvictIdle()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _entries.ToArray())
        {
            if (RedisPoolPolicy.IsIdle(kv.Value.LastUsed, now, IdleTimeout))
            {
                if (_entries.TryRemove(kv.Key, out var removed))
                {
                    try { removed.Multiplexer.Dispose(); } catch { }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _evictor.Dispose();
        foreach (var kv in _entries)
        {
            try { kv.Value.Multiplexer.Dispose(); } catch { }
        }
        _entries.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}