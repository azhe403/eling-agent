using Eling.Core.Database;
using StackExchange.Redis;

namespace Eling.Core.Tests;

/// <summary>
/// Spec 6 rule 6: <c>SCAN</c> is never executed raw through the console, so
/// <c>COUNT 100000000</c> cannot reach the server. These pin the part of that
/// rule that holds without a live server - the argument handling, and the fact
/// that a refused pattern never reaches the pool at all.
///
/// What is NOT covered here: that a successful scan returns leaves rather than
/// tree nodes. That needs a multiplexer serving a real keyspace, and the pool
/// interface hands out an IConnectionMultiplexer whose server-side scan cannot
/// be faked without a live Redis. Saying so is the point - the earlier version
/// of this behaviour shipped the folders as keys and no test noticed.
/// </summary>
public sealed class RedisScanCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eling-redis-scan-tests", Guid.NewGuid().ToString("N"));

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

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>
    /// Counts calls so a test can prove the guard ran before the service tried
    /// to reach a server.
    /// </summary>
    private sealed class CountingPool : IRedisConnectionPool
    {
        public int Calls { get; private set; }

        public Task<IConnectionMultiplexer> GetAsync(DatabaseConnection connection, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromException<IConnectionMultiplexer>(new TimeoutException("no connection is available"));
        }

        public void Invalidate(string connectionId)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private (RedisKeyReadService Service, CountingPool Pool) NewService()
    {
        var pool = new CountingPool();
        return (new RedisKeyReadService(pool, new JsonRedisReadCache(_dir)), pool);
    }

    [Theory]
    [InlineData("user:?:x")]
    [InlineData("user:[ab]")]
    [InlineData("user:*:x")]
    public async Task Scan_WithAGlobThatIsNotAPrefix_IsRefusedWithoutTouchingTheServer(string pattern)
    {
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        var (service, pool) = NewService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ExecuteCommandAsync(Connection(), db, "SCAN", ["0", "MATCH", pattern]));

        // The refusal has to happen before the pool is asked for a connection,
        // or the guard is decoration.
        Assert.Equal(0, pool.Calls);
    }

    [Fact]
    public async Task Scan_WithAnUnsupportedPattern_ExplainsWhereToGoInstead()
    {
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        var (service, _) = NewService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.ExecuteCommandAsync(Connection(), db, "SCAN", ["0", "MATCH", "a:?:b"]));

        Assert.Contains("key tree", ex.Message);
    }

    [Fact]
    public async Task Scan_IsNeverSentAsTheRawCommand()
    {
        // A prefix pattern is accepted, so the service proceeds to the paging
        // path and only then fails on the dead pool. The point is the failure
        // is the pool's TimeoutException, not a successful raw execution - the
        // service has no server to run "SCAN 0 COUNT 100000000" against.
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        var (service, pool) = NewService();

        await Assert.ThrowsAsync<TimeoutException>(
            () => service.ExecuteCommandAsync(Connection(), db, "SCAN", ["0", "MATCH", "user:*", "COUNT", "100000000"]));

        Assert.Equal(1, pool.Calls);
    }

    [Fact]
    public async Task Scan_LowercaseCommand_StillTakesTheRewrittenPath()
    {
        // The dispatch compares the upper-cased command; a console user typing
        // "scan" must get the same guard as "SCAN", not the raw path.
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        var (service, pool) = NewService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ExecuteCommandAsync(Connection(), db, "scan", ["0", "MATCH", "a:?:b"]));

        Assert.Equal(0, pool.Calls);
    }
}
