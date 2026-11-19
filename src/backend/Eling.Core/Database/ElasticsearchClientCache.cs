using System.Collections.Concurrent;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Database;

/// <summary>
/// Cache of <see cref="ElasticsearchClient"/> instances keyed by connection id,
/// storing the connection string per entry. Same id and same string reuse the
/// client; a changed string rebuilds it. Bounded to 8 entries (LRU eviction) with
/// a five-minute idle sweep, mirroring <see cref="RedisConnectionPool"/>.
/// </summary>
public sealed class ElasticsearchClientCache : IElasticsearchClientCache, IDisposable
{
    private const int MaxConnections = 8;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public required string ConnectionString { get; init; }
        public required ElasticsearchClient Client { get; init; }
        public DateTimeOffset LastUsed { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Timer _evictor;
    private readonly ILogger<ElasticsearchClientCache>? _logger;
    private bool _disposed;

    /// <param name="evictionInterval">
    /// How often the idle sweep runs. Tests pass a long or disabled interval so a
    /// sweep never races their assertions.
    /// </param>
    public ElasticsearchClientCache(TimeSpan? evictionInterval = null, ILogger<ElasticsearchClientCache>? logger = null)
    {
        var interval = evictionInterval ?? TimeSpan.FromMinutes(1);
        _logger = logger;
        _evictor = new Timer(_ => EvictIdle(), null, interval, interval);
    }

    public ElasticsearchClient GetOrCreate(DatabaseConnection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_entries.TryGetValue(connection.Id, out var existing))
        {
            if (string.Equals(existing.ConnectionString, connection.ConnectionString, StringComparison.Ordinal))
            {
                existing.LastUsed = DateTimeOffset.UtcNow;
                return existing.Client;
            }

            // Connection string changed: drop the stale client and build a fresh one.
            if (_entries.TryRemove(connection.Id, out var removed))
            {
                DisposeBestEffort(removed);
            }
        }

        if (_entries.Count >= MaxConnections)
        {
            var lastUsed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var pair in _entries)
            {
                lastUsed[pair.Key] = pair.Value.LastUsed;
            }

            var lruKey = RedisPoolPolicy.PickLeastRecentlyUsed(lastUsed);
            if (lruKey is not null && _entries.TryRemove(lruKey, out var evicted))
            {
                DisposeBestEffort(evicted);
            }
        }

        var client = BuildClient(connection.ConnectionString);
        _entries[connection.Id] = new Entry
        {
            ConnectionString = connection.ConnectionString,
            Client = client,
            LastUsed = DateTimeOffset.UtcNow,
        };
        return client;
    }

    public void Invalidate(string connectionId)
    {
        if (_entries.TryRemove(connectionId, out var removed))
        {
            DisposeBestEffort(removed);
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
                    DisposeBestEffort(removed);
                }
            }
        }
    }

    private void DisposeBestEffort(Entry entry)
    {
        // ElasticsearchClient (8.19.x) exposes no public disposal handle for its
        // transport; the only lever is dropping the reference and letting the GC
        // reclaim it. Keep the seam so a future client version with a public
        // IDisposable gets disposed here without touching call sites.
        try
        {
            (entry.Client.Transport as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Elasticsearch client dispose failed; dropping the reference");
        }
    }

    private static ElasticsearchClient BuildClient(string connectionString)
    {
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var rawUri)
            || (rawUri.Scheme != "http" && rawUri.Scheme != "https"))
        {
            throw new ArgumentException(
                $"'{connectionString}' is not a valid Elasticsearch connection string (http/https required).",
                nameof(connectionString));
        }

        // Strip credentials from the node address: the transport must not echo
        // userinfo into request URIs, logs or the audit trail. Basic auth travels
        // via the dedicated header instead.
        var nodeUri = new UriBuilder(rawUri) { UserName = string.Empty, Password = string.Empty }.Uri;

        var settings = new ElasticsearchClientSettings(nodeUri)
            .RequestTimeout(TimeSpan.FromSeconds(5))
            // Buffer responses so the service can read the raw cluster payload for
            // verbatim aggregation passthrough. Pages are capped far below memory pressure.
            .DisableDirectStreaming(true);

        if (TrySplitUserInfo(rawUri.UserInfo, out var username, out var password))
        {
            settings = settings.Authentication(
                new Elastic.Transport.BasicAuthentication(username, password ?? string.Empty));
        }

        return new ElasticsearchClient(settings);
    }

    private static bool TrySplitUserInfo(string? userInfo, out string username, out string? password)
    {
        username = string.Empty;
        password = null;
        if (string.IsNullOrEmpty(userInfo))
        {
            return false;
        }

        var separator = userInfo.IndexOf(':');
        try
        {
            if (separator < 0)
            {
                username = Uri.UnescapeDataString(userInfo);
                return username.Length > 0;
            }

            username = Uri.UnescapeDataString(userInfo[..separator]);
            password = Uri.UnescapeDataString(userInfo[(separator + 1)..]);
            return username.Length > 0;
        }
        catch (UriFormatException)
        {
            username = string.Empty;
            password = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _evictor.Dispose();
        foreach (var kv in _entries)
        {
            DisposeBestEffort(kv.Value);
        }

        _entries.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
