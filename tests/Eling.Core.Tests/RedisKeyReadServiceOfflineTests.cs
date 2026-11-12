using Eling.Core.Database;
using StackExchange.Redis;

namespace Eling.Core.Tests;

/// <summary>
/// The point of the Redis read cache is a server that cannot be reached. These
/// tests stand a pool in front of the service that fails the way a dead server
/// fails, and pin what the reader gets back: the cached answer, marked cached,
/// or the original failure when nothing was cached. ListDatabases, Scan and
/// Inspect all follow the same offline-fallback standard.
/// </summary>
public sealed class RedisKeyReadServiceOfflineTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eling-redis-offline-tests", Guid.NewGuid().ToString("N"));

    private static DatabaseConnection Connection() => new(
        Id: "c1",
        Name: "cache",
        Provider: DatabaseProvider.Redis,
        ConnectionString: "localhost:6379",
        Environment: DatabaseEnvironment.Development,
        AllowAgentMutations: false,
        RequireApprovalForMutations: true,
        CreatedAt: DateTimeOffset.UtcNow,
        LastConnectedAt: null);

    private static RedisKeyReadService Service(IRedisReadCache cache) =>
        // No logger: the fallback logs a warning, and a test needs no sink.
        new(new DeadPool(), cache);

    private JsonRedisReadCache NewCache() => new(_dir);

    private static RedisDatabaseInfo[] Databases() => [new(0, 3), new(3, 128)];

    [Fact]
    public async Task Inspect_WhenTheServerIsDownAndTheKeyWasCached_AnswersFromCache()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(3, out var db));
        cache.SetKey("c1", db, Result("session:abc"));

        var result = await Service(cache).InspectAsync(Connection(), db, "session:abc");

        Assert.True(result.FromCache);
        Assert.NotNull(result.CachedAt);
        Assert.Equal("session:abc", result.Key);
        Assert.Equal("string", result.Type);
    }

    [Fact]
    public async Task Inspect_WhenTheServerIsDownAndNothingWasCached_Throws()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            Service(cache).InspectAsync(Connection(), db, "never-seen"));
    }

    [Fact]
    public async Task Databases_WhenTheServerIsDownAndTheListWasCached_AnswersFromCache()
    {
        var cache = NewCache();
        cache.SetDatabases("c1", Databases());

        var result = await Service(cache).ListDatabasesAsync(Connection());

        Assert.All(result, db => Assert.True(db.FromCache));
        Assert.All(result, db => Assert.NotNull(db.CachedAt));
        Assert.Equal([0, 3], result.Select(db => db.Index));
    }

    [Fact]
    public async Task Databases_WhenTheServerIsDownAndNothingWasCached_Throws()
    {
        var cache = NewCache();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            Service(cache).ListDatabasesAsync(Connection()));
    }

    private static KeyPage ScanPage(string prefix) => new(
        prefix,
        [new KeyNode("session", "session:")],
        [new KeyLeaf("session:abc", "string", -1)],
        Cursor: 0,
        PageOffset: 0,
        Complete: true,
        Scanned: 1);

    [Fact]
    public async Task Scan_WhenTheServerIsDownAndThePageWasCached_AnswersFromCache()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        cache.SetPage("c1", db, "session:", 0, 0, 200, ScanPage("session:"));

        var result = await Service(cache).ScanAsync(Connection(), db, "session:", 0, 0, 200);

        Assert.True(result.FromCache);
        Assert.NotNull(result.CachedAt);
        Assert.Equal(["session:abc"], result.Keys.Select(k => k.Key));
    }

    [Fact]
    public async Task Scan_WhenTheServerIsDownAndNothingWasCached_Throws()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            Service(cache).ScanAsync(Connection(), db, "", 0, 0, 200));
    }

    [Fact]
    public async Task Inspect_WhenTheServerIsDown_ReportsTheTtlLeftFromTheAbsoluteExpiry()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        // Stored as if read ten minutes ago with an hour left: the snapshot
        // says 3600, but only fifty minutes of the key actually remain.
        cache.SetKey("c1", db, new KeyInspectResult(
            "session:abc", "string", 3600,
            DateTimeOffset.UtcNow.AddMinutes(50), 5, "embstr",
            new KeyInspectPreview("string", [], false)));

        var result = await Service(cache).InspectAsync(Connection(), db, "session:abc");

        Assert.True(result.FromCache);
        Assert.InRange(result.TtlSeconds, 49 * 60, 50 * 60);
    }

    [Fact]
    public async Task Inspect_WhenRecentlyMarkedUnreachable_ServesTheCachedAnswerWithoutHittingThePool()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        var pool = new CountingDeadPool();
        var service = new RedisKeyReadService(pool, cache);

        // First call: nothing cached, the server is down - it fails, and the
        // connection is marked unreachable.
        await Assert.ThrowsAsync<TimeoutException>(() => service.InspectAsync(Connection(), db, "session:abc"));
        Assert.Equal(1, pool.GetAsyncCount);

        // Cache lands. The second call must not touch the pool at all while
        // the unreachable window is still open - it costs exactly nothing.
        cache.SetKey("c1", db, Result("session:abc"));
        var result = await service.InspectAsync(Connection(), db, "session:abc");
        Assert.True(result.FromCache);
        Assert.Equal(1, pool.GetAsyncCount);
    }

    [Fact]
    public async Task Inspect_WhenTheServerRejectsTheCommand_ServesTheCachedSnapshot()
    {
        // The dashboard paints the snapshot like it does for schema and rows;
        // the server is not even asked when a snapshot exists. A reachable but
        // rejecting server is not a reason to hold the reader waiting for it.
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        cache.SetKey("c1", db, Result("session:abc"));

        var service = new RedisKeyReadService(new RejectingPool(), cache);

        var result = await service.InspectAsync(Connection(), db, "session:abc");
        Assert.True(result.FromCache);
        Assert.Equal("session:abc", result.Key);
    }

    [Fact]
    public async Task Inspect_WhenThePasswordIsWrong_ServesTheCachedSnapshot()
    {
        // Same contract: the dashboard paints from the snapshot; a server that
        // answers with an auth error is not consulted again for a refresh=true.
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        cache.SetKey("c1", db, Result("session:abc"));

        var service = new RedisKeyReadService(new WrongPasswordPool(), cache);

        var result = await service.InspectAsync(Connection(), db, "session:abc");
        Assert.True(result.FromCache);
        Assert.Equal("session:abc", result.Key);
    }

    private static KeyInspectResult Result(string key) =>
        new(key, "string", -1, null, 5, "embstr", new KeyInspectPreview("string", [], false));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A pool whose server is always down.</summary>
    private sealed class DeadPool : IRedisConnectionPool
    {
        public Task<IConnectionMultiplexer> GetAsync(DatabaseConnection connection, CancellationToken ct = default) =>
            // What the pool surfaces when the server cannot be reached in time:
            // a timeout, which the service treats as "unreachable", not as an
            // answer. (The real one is RedisTimeoutException, a TimeoutException.)
            Task.FromException<IConnectionMultiplexer>(new TimeoutException("no connection is available"));

        public void Invalidate(string connectionId)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // The only way to fabricate the two exceptions the service must classify is
    // their internal-ish constructors, which StackExchange.Redis marks obsolete
    // for callers that can supply the extra metadata. A test cannot.
#pragma warning disable CS0618
    /// <summary>A pool whose server is always down, counting how often it is asked.</summary>
    private sealed class CountingDeadPool : IRedisConnectionPool
    {
        public int GetAsyncCount { get; private set; }

        public Task<IConnectionMultiplexer> GetAsync(DatabaseConnection connection, CancellationToken ct = default)
        {
            GetAsyncCount++;
            return Task.FromException<IConnectionMultiplexer>(new TimeoutException("no connection is available"));
        }

        public void Invalidate(string connectionId)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A server that is up and refuses the command itself.</summary>
    private sealed class RejectingPool : IRedisConnectionPool
    {
        public Task<IConnectionMultiplexer> GetAsync(DatabaseConnection connection, CancellationToken ct = default) =>
            Task.FromException<IConnectionMultiplexer>(new RedisServerException("WRONGTYPE Operation against a key"));

        public void Invalidate(string connectionId)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A server that is up and rejects our password.</summary>
    private sealed class WrongPasswordPool : IRedisConnectionPool
    {
        public Task<IConnectionMultiplexer> GetAsync(DatabaseConnection connection, CancellationToken ct = default) =>
            Task.FromException<IConnectionMultiplexer>(
                new RedisConnectionException(ConnectionFailureType.AuthenticationFailure, "WRONGPASS invalid username-password pair"));

        public void Invalidate(string connectionId)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore CS0618
}