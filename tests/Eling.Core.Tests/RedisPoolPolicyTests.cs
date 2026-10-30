using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The pool holds at most sixteen multiplexers and drops one after five idle
/// minutes (spec §2.2). Both rules are decided by pure functions here, so they
/// are pinned without a Redis server and without waiting for a timer.
/// </summary>
public sealed class RedisPoolPolicyTests
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public void IsIdle_JustInsideTheWindow_IsNotIdle()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(RedisPoolPolicy.IsIdle(now - TimeSpan.FromMinutes(4), now, IdleTimeout));
    }

    [Fact]
    public void IsIdle_AtTheCutoff_IsIdle()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(RedisPoolPolicy.IsIdle(now - IdleTimeout, now, IdleTimeout));
    }

    [Fact]
    public void IsIdle_WellPastTheWindow_IsIdle()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(RedisPoolPolicy.IsIdle(now - TimeSpan.FromMinutes(30), now, IdleTimeout));
    }

    [Fact]
    public void PickLeastRecentlyUsed_ChoosesTheOldest()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["a"] = now,
            ["b"] = now - TimeSpan.FromMinutes(9),
            ["c"] = now - TimeSpan.FromMinutes(2),
        };

        Assert.Equal("b", RedisPoolPolicy.PickLeastRecentlyUsed(entries));
    }

    [Fact]
    public void PickLeastRecentlyUsed_NothingToEvict_ReturnsNull()
    {
        Assert.Null(RedisPoolPolicy.PickLeastRecentlyUsed(new Dictionary<string, DateTimeOffset>()));
    }

    [Fact]
    public void PickLeastRecentlyUsed_ASingleEntry_IsThatEntry()
    {
        var entries = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["only"] = DateTimeOffset.UtcNow,
        };

        Assert.Equal("only", RedisPoolPolicy.PickLeastRecentlyUsed(entries));
    }

    [Fact]
    public void Pool_CanBeConstructedWithItsOwnEvictionInterval()
    {
        // The interval is injectable so a caller (or a test) can keep the
        // sweep from racing what it is checking.
        using var pool = new RedisConnectionPool(TimeSpan.FromHours(1));

        Assert.NotNull(pool);
    }
}
