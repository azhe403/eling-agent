using System.Net;
using System.Reflection;
using Eling.Core.Database;
using StackExchange.Redis;

namespace Eling.Core.Tests;

/// <summary>
/// Dashboard-only Redis deletes: single-key delete, the capped prefix preview the
/// subtree dialog lists, and the subtree delete itself. Each test stands a fake
/// pool in front of the service — DispatchProxy fakes for the multiplexer,
/// database and server so only the overloads production binds are answered —
/// and a counting cache so eviction is proved. Budgets are the read path's:
/// 2000 keys scanned, 3000 ms; the preview list caps at 100.
/// </summary>
public sealed class RedisKeyDeleteTests
{
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

    private static RedisDatabaseIndex Database0()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        return db;
    }

    [Fact]
    public async Task DeleteKey_ExistingKey_RemovesAndReturnsTrue()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var harness = NewHarness(["user:1:email"]);
        harness.DatabaseProxy.DeleteResult = true;

        var result = await harness.Service.DeleteKeyAsync(Connection(), db, "user:1:email");

        Assert.True(result.Deleted);
        Assert.Equal(["user:1:email"], harness.Cache.RemovedKeys);
    }

    [Fact]
    public async Task DeleteKey_KeyWithSlashAndColon_ReachesRedisIntact()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var harness = NewHarness(["a/b:c"]);
        harness.DatabaseProxy.DeleteResult = true;

        await harness.Service.DeleteKeyAsync(Connection(), db, "a/b:c");

        Assert.Equal(["a/b:c"], harness.DatabaseProxy.DeletedKeys);
    }

    [Fact]
    public async Task DeleteKey_MissingKey_ThrowsKeyNotFound()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var harness = NewHarness([]);
        harness.DatabaseProxy.DeleteResult = false;

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => harness.Service.DeleteKeyAsync(Connection(), db, "missing:key"));

        Assert.Equal("key not found", ex.Message);
        Assert.Equal(["missing:key"], harness.Cache.RemovedKeys);
    }

    [Fact]
    public async Task DeleteKey_BlankKey_RefusedBeforePool()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var harness = NewHarness(["user:1:email"]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.DeleteKeyAsync(Connection(), db, " "));

        Assert.Equal("key must not be blank", ex.Message);
        Assert.Equal(0, harness.Pool.GetAsyncCalls);
    }

    [Fact]
    public async Task PreviewPrefix_SmallPrefix_ReturnsKeysTotalTruncatedFalse()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var harness = NewHarness(["user:1:email", "user:1:name"]);

        var result = await harness.Service.PreviewPrefixAsync(Connection(), db, "user:");

        Assert.Equal("user:", result.Prefix);
        Assert.Equal(["user:1:email", "user:1:name"], result.Keys);
        Assert.Equal(2, result.Total);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task PreviewPrefix_LargePrefix_CapsListAt100AndFlagsTruncated()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var keys = Enumerable.Range(0, 150).Select(i => $"user:{i}").ToList();
        var harness = NewHarness(keys);

        var result = await harness.Service.PreviewPrefixAsync(Connection(), db, "user:");

        Assert.Equal(100, result.Keys.Count);
        Assert.True(result.Total >= 100);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task DeletePrefix_EmptyPrefix_RefusedBeforePool()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var harness = NewHarness(["user:1:email"]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.DeletePrefixAsync(Connection(), db, ""));

        Assert.Equal("prefix must not be blank", ex.Message);
        Assert.Equal(0, harness.Pool.GetAsyncCalls);
    }

    [Fact]
    public async Task DeletePrefix_BudgetHit_ReportsTruncated()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var keys = Enumerable.Range(0, 2500).Select(i => $"user:{i}").ToList();
        var harness = NewHarness(keys);
        harness.DatabaseProxy.DeleteResult = true;

        var result = await harness.Service.DeletePrefixAsync(Connection(), db, "user:");

        Assert.True(result.Deleted <= 2000);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task DeleteKey_Unreachable_PropagatesWithoutEvicting()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var cache = new CountingCache();
        var service = new RedisKeyReadService(new FakePool(new TimeoutException("no connection")), cache);

        await Assert.ThrowsAsync<TimeoutException>(
            () => service.DeleteKeyAsync(Connection(), db, "user:1:email"));

        Assert.Empty(cache.RemovedKeys);
    }

    [Fact]
    public async Task DeleteKey_CredentialFailure_PropagatesAsItself()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var cache = new CountingCache();
#pragma warning disable CS0618
        var failure = new RedisConnectionException(
            ConnectionFailureType.AuthenticationFailure, "WRONGPASS invalid username-password pair");
#pragma warning restore CS0618
        var service = new RedisKeyReadService(new FakePool(failure), cache);

        await Assert.ThrowsAsync<RedisConnectionException>(
            () => service.DeleteKeyAsync(Connection(), db, "user:1:email"));

        Assert.Empty(cache.RemovedKeys);
    }

    [Fact]
    public async Task PreviewPrefix_Unreachable_Propagates()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var harness = NewHarness([]);
        harness.ServerProxy.ScanError = new TimeoutException("no connection");

        await Assert.ThrowsAsync<TimeoutException>(
            () => harness.Service.PreviewPrefixAsync(Connection(), db, "user:"));
    }

    [Fact]
    public async Task DeletePrefix_ScanUnreachable_Propagates()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var harness = NewHarness([]);
        harness.ServerProxy.ScanError = new TimeoutException("no connection");

        await Assert.ThrowsAsync<TimeoutException>(
            () => harness.Service.DeletePrefixAsync(Connection(), db, "user:"));
    }

    [Fact]
    public async Task DeletePrefix_DeleteUnreachable_PropagatesWithoutEvicting()
    {
        RedisDatabaseIndex.TryCreate(0, out var db);
        var harness = NewHarness(["user:1:email"]);
        harness.DatabaseProxy.DeleteError = new TimeoutException("no connection");

        await Assert.ThrowsAsync<TimeoutException>(
            () => harness.Service.DeletePrefixAsync(Connection(), db, "user:"));

        Assert.Empty(harness.Cache.RemovedKeys);
    }

    // --- fakes -----------------------------------------------------------------

    /// <summary>One test's wired-up service and its doubles, bundled so each test reads as arranged.</summary>
    private sealed record Harness(
        RedisKeyReadService Service,
        FakePool Pool,
        CountingCache Cache,
        DatabaseProxy DatabaseProxy,
        ServerProxy ServerProxy);

    private static Harness NewHarness(IReadOnlyList<string> scanKeys)
    {
        IDatabase database = DispatchProxy.Create<IDatabase, DatabaseProxy>();
        var databaseCast = (DatabaseProxy)(object)database;

        var serverProxyCast = (ServerProxy)(object)DispatchProxy.Create<IServer, ServerProxy>();
        IServer server = (IServer)(object)serverProxyCast;
        serverProxyCast.Keys = [.. scanKeys.Select(k => (RedisKey)k)];

        var muxProxyCast = (MuxProxy)(object)DispatchProxy.Create<IConnectionMultiplexer, MuxProxy>();
        IConnectionMultiplexer mux = (IConnectionMultiplexer)(object)muxProxyCast;
        muxProxyCast.Database = database;
        muxProxyCast.Server = server;

        var pool = new FakePool(mux);
        var cache = new CountingCache();
        var service = new RedisKeyReadService(pool, cache);
        return new Harness(service, pool, cache, databaseCast, serverProxyCast);
    }

    /// <summary>Counts <see cref="IRedisConnectionPool.GetAsync"/> so refusals can prove the pool was never touched.</summary>
    private sealed class FakePool : IRedisConnectionPool
    {
        private readonly IConnectionMultiplexer? _mux;
        private readonly Exception? _error;

        public FakePool(IConnectionMultiplexer mux) => _mux = mux;

        public FakePool(Exception error) => _error = error;

        public int GetAsyncCalls { get; private set; }

        public Task<IConnectionMultiplexer> GetAsync(DatabaseConnection connection, CancellationToken ct = default)
        {
            GetAsyncCalls++;
            return _error is not null
                ? Task.FromException<IConnectionMultiplexer>(_error)
                : Task.FromResult(_mux!);
        }

        public void Invalidate(string connectionId)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records every <see cref="IRedisReadCache.RemoveKey"/> instead of touching a file.</summary>
    private sealed class CountingCache : IRedisReadCache
    {
        public List<string> RemovedKeys { get; } = [];

        public bool TryGetDatabases(
            string connectionId,
            out IReadOnlyList<RedisDatabaseInfo>? value,
            out DateTimeOffset? cachedAt)
        {
            value = null;
            cachedAt = null;
            return false;
        }

        public void SetDatabases(string connectionId, IReadOnlyList<RedisDatabaseInfo> value)
        {
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
            return false;
        }

        public void SetKey(string connectionId, RedisDatabaseIndex database, KeyInspectResult value)
        {
        }

        public void RemoveKey(string connectionId, RedisDatabaseIndex database, string key) =>
            RemovedKeys.Add(key);

        public bool TryGetPage(
            string connectionId,
            RedisDatabaseIndex database,
            string prefix,
            long cursor,
            int pageOffset,
            int pageSize,
            out KeyPage? value,
            out DateTimeOffset? cachedAt)
        {
            value = null;
            cachedAt = null;
            return false;
        }

        public void SetPage(
            string connectionId,
            RedisDatabaseIndex database,
            string prefix,
            long cursor,
            int pageOffset,
            int pageSize,
            KeyPage value)
        {
        }

        public void InvalidateConnection(string connectionId)
        {
        }
    }

    /// <summary>
    /// Answers only the two-argument <c>KeyDeleteAsync(RedisKey, CommandFlags)</c>
    /// the service binds; everything else is a loud failure.
    /// </summary>
    private class DatabaseProxy : DispatchProxy
    {
        public bool DeleteResult { get; set; }

        public Exception? DeleteError { get; set; }

        public List<string> DeletedKeys { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDatabaseAsync.KeyDeleteAsync) && args?.Length == 2)
            {
                if (DeleteError is not null)
                {
                    return Task.FromException<bool>(DeleteError);
                }

                DeletedKeys.Add(((RedisKey)args[0]!).ToString());
                return Task.FromResult(DeleteResult);
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    /// <summary>
    /// Answers <c>ServerType</c> (standalone) and the six-argument
    /// <c>KeysAsync</c> the service binds; everything else is a loud failure.
    /// </summary>
    private class ServerProxy : DispatchProxy
    {
        public List<RedisKey> Keys { get; set; } = [];

        public Exception? ScanError { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_ServerType")
            {
                return ServerType.Standalone;
            }

            if (targetMethod?.Name == nameof(IServer.KeysAsync))
            {
                return ScanError is not null
                    ? new ThrowingEnumerable(ScanError)
                    : new KeyEnumerable(Keys);
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    /// <summary>Hands out the configured database and server, and one loopback endpoint for <c>GetServer</c> to resolve.</summary>
    private class MuxProxy : DispatchProxy
    {
        public IDatabase Database { get; set; } = null!;

        public IServer Server { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(IConnectionMultiplexer.GetDatabase) => Database,
                nameof(IConnectionMultiplexer.GetServer) => Server,
                nameof(IConnectionMultiplexer.GetEndPoints) => new EndPoint[]
                {
                    new IPEndPoint(IPAddress.Loopback, 6379)
                },
                _ => throw new NotImplementedException(targetMethod?.Name)
            };
        }
    }

    /// <summary>A minimal async sequence for the fake scan; the service drives it to completion or budget.</summary>
    private sealed class KeyEnumerable : IAsyncEnumerable<RedisKey>
    {
        private readonly List<RedisKey> _keys;

        public KeyEnumerable(List<RedisKey> keys) => _keys = keys;

        public async IAsyncEnumerator<RedisKey> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var key in _keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return key;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>A fake scan whose first page never arrives: the server stops answering mid-scan.</summary>
    private sealed class ThrowingEnumerable : IAsyncEnumerable<RedisKey>
    {
        private readonly Exception _error;

        public ThrowingEnumerable(Exception error) => _error = error;

        public IAsyncEnumerator<RedisKey> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new ThrowingEnumerator(_error);
    }

    private sealed class ThrowingEnumerator : IAsyncEnumerator<RedisKey>
    {
        private readonly Exception _error;

        public ThrowingEnumerator(Exception error) => _error = error;

        public RedisKey Current => throw _error;

        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(_error);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
