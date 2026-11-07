using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// A fresh schema answer says nothing about caching, so it must look exactly
/// like a live read: source <c>live</c>, no expiry, no TTL. The cached
/// answers carry the same three facts, so the dashboard can tell "the database
/// answered" from "a snapshot answered and the database is down" without
/// guessing from the age alone.
/// </summary>
public sealed class DatabaseSchemaInfoTests
{
    [Fact]
    public void Fresh_DefaultsToLive_WithNoExpiry()
    {
        var info = new DatabaseSchemaInfo([]);

        Assert.Equal(CacheSources.Live, info.Source);
        Assert.False(info.FromCache);
        Assert.Null(info.CacheExpiresAt);
        Assert.Null(info.CacheTtlSeconds);
    }

    [Fact]
    public void Refreshing_MarksFromCache_AndCarriesExpiry()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(6);

        var info = new DatabaseSchemaInfo([])
        {
            Source = CacheSources.Refreshing,
            CacheExpiresAt = expires,
            CacheTtlSeconds = 3600
        };

        Assert.True(info.FromCache);
        Assert.Equal(expires, info.CacheExpiresAt);
        Assert.Equal(3600, info.CacheTtlSeconds);
    }

    [Fact]
    public void Offline_MarksFromCache()
    {
        var info = new DatabaseSchemaInfo([]) { Source = CacheSources.Offline };

        Assert.True(info.FromCache);
    }
}
