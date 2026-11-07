using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// A fresh schema answer says nothing about caching, so it must look exactly
/// like a live read: source <c>live</c>, no expiry, no TTL. A snapshot that
/// answered for an unreachable database carries its remaining lifetime, so the
/// dashboard can tell "the database answered" from "a snapshot answered and the
/// database is down".
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
    public void Offline_MarksFromCache_AndCarriesExpiry()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(6);

        var info = new DatabaseSchemaInfo([])
        {
            Source = CacheSources.Offline,
            CacheExpiresAt = expires,
            CacheTtlSeconds = 3600
        };

        Assert.True(info.FromCache);
        Assert.Equal(expires, info.CacheExpiresAt);
        Assert.Equal(3600, info.CacheTtlSeconds);
    }
}
