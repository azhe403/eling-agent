using System.Text.Json;

using Eling.Core.Database;
using Eling.Core.Serialization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Core.Tests;

/// <summary>
/// The read path answers from the cache without touching the database while a
/// snapshot is fresh, goes to the database for an older one, and falls back to
/// the snapshot when the database is down. These tests pin that order with a
/// database that cannot be opened: a fresh snapshot must still answer as
/// <c>live</c>, and an old one must answer as <c>refreshing</c> or
/// <c>offline</c> — never by throwing.
/// </summary>
public sealed class DatabaseReadServiceCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eling-read-service-tests", Guid.NewGuid().ToString("N"));

    private DatabaseReadService NewService()
        => new(new JsonDatabaseReadCache(_dir), NullLogger<DatabaseReadService>.Instance);

    /// <summary>A SQLite connection string pointing at a directory: opening it fails.</summary>
    private DatabaseConnection BrokenConnection()
    {
        var notADatabase = Path.Combine(_dir, "a-directory");
        Directory.CreateDirectory(notADatabase);
        return DatabaseConnection.Create(
            "broken@local",
            DatabaseProvider.Sqlite,
            new SqliteConnectionStringBuilder { DataSource = notADatabase }.ToString(),
            DatabaseEnvironment.ReadOnly);
    }

    private static DatabaseSchemaInfo Schema(params string[] tables)
        => new([.. tables.Select(t => new TableSchema(t, "table", [], [], []))]);

    /// <summary>Write a schema entry with a chosen age, bypassing the writer.</summary>
    private void SeedSchema(DatabaseConnection connection, DateTimeOffset cachedAt, DatabaseSchemaInfo schema)
    {
        var cache = new JsonDatabaseReadCache(_dir);
        cache.SetSchema(connection.Id, null, schema);

        var path = Path.Combine(_dir, "database-schema-cache.json");
        var file = JsonSerializer.Deserialize<DatabaseSchemaCacheFile>(
            File.ReadAllText(path), JsonDefaults.Shared)!;
        var aged = file with
        {
            Schemas = [.. file.Schemas.Select(s => s with { CachedAt = cachedAt })]
        };
        File.WriteAllText(path, JsonSerializer.Serialize(aged, JsonDefaults.Shared));
    }

    [Fact]
    public async Task FreshSnapshot_IsServedAsLive_WithoutTouchingTheDatabase()
    {
        var connection = BrokenConnection();
        SeedSchema(connection, DateTimeOffset.UtcNow, Schema("users"));

        var result = await NewService().GetSchemaAsync(connection, null);

        Assert.Equal(CacheSources.Live, result.Source);
        Assert.False(result.FromCache);
        Assert.Equal("users", Assert.Single(result.Tables).Name);
    }

    [Fact]
    public async Task OldSnapshot_IsServedAsRefreshing_WhileTheRefreshRuns()
    {
        var connection = BrokenConnection();
        SeedSchema(connection, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5), Schema("users"));

        var result = await NewService().GetSchemaAsync(connection, null);

        Assert.Equal(CacheSources.Refreshing, result.Source);
        Assert.Equal("users", Assert.Single(result.Tables).Name);
    }

    [Fact]
    public async Task OldSnapshot_BecomesOffline_OnceTheRefreshHasFailed()
    {
        var connection = BrokenConnection();
        SeedSchema(connection, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5), Schema("users"));

        var service = NewService();
        Assert.Equal(CacheSources.Refreshing, (await service.GetSchemaAsync(connection, null)).Source);

        // The background refresh fails on the broken connection; the next read
        // reports the snapshot as offline rather than probing again.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        DatabaseSchemaInfo? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await service.GetSchemaAsync(connection, null);
            if (last.Source == CacheSources.Offline) break;
            await Task.Delay(100);
        }

        Assert.Equal(CacheSources.Offline, last!.Source);
        Assert.Equal("users", Assert.Single(last.Tables).Name);
    }

    [Fact]
    public async Task PreferCacheFalse_GoesToTheDatabase_EvenWhenTheSnapshotIsFresh()
    {
        var connection = BrokenConnection();
        SeedSchema(connection, DateTimeOffset.UtcNow, Schema("users"));

        var result = await NewService().GetSchemaAsync(connection, null, preferCache: false);

        // The snapshot was fresh, but the caller asked for the database. It was
        // unreachable, so the answer is the snapshot marked offline — not the
        // fresh snapshot served as live.
        Assert.Equal(CacheSources.Offline, result.Source);
        Assert.Equal("users", Assert.Single(result.Tables).Name);
    }

    [Fact]
    public async Task CachedAnswer_CarriesAConsistentTtl()
    {
        var connection = BrokenConnection();
        var cachedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        SeedSchema(connection, cachedAt, Schema("users"));

        var result = await NewService().GetSchemaAsync(connection, null);

        Assert.Equal(cachedAt, result.CachedAt);
        Assert.Equal(cachedAt + IDatabaseReadCache.DefaultMaxAge, result.CacheExpiresAt);
        Assert.NotNull(result.CacheTtlSeconds);
        var expected = (long)Math.Max(0, (result.CacheExpiresAt!.Value - DateTimeOffset.UtcNow).TotalSeconds);
        Assert.InRange(result.CacheTtlSeconds!.Value, expected - 5, expected + 5);
    }

    [Fact]
    public async Task ASnapshotInsideTheWindow_IsServedAsLive_AndStillRefreshedInTheBackground()
    {
        // A snapshot that is still current is answered without waiting for the
        // database. But nothing else would refresh it, so its timestamp would
        // sit at the edge of the window and read as stale soon. A snapshot
        // that has been sitting a while is therefore refreshed on the way out:
        // the answer is immediate, and it stays honestly current.
        var db = Path.Combine(_dir, "real.db");
        Directory.CreateDirectory(_dir);
        await using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT);";
            await cmd.ExecuteNonQueryAsync();
        }

        var connection = DatabaseConnection.Create(
            "real@local",
            DatabaseProvider.Sqlite,
            $"Data Source={db}",
            DatabaseEnvironment.ReadOnly);

        // Age the entry past the refresh-due point but keep it inside the
        // freshness window.
        var seededAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(15);
        SeedSchema(connection, seededAt, Schema("users"));

        var result = await NewService().GetSchemaAsync(connection, null);

        Assert.Equal(CacheSources.Live, result.Source);
        Assert.False(result.FromCache);

        // The background refresh moves the timestamp on.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        DateTimeOffset cachedAt = seededAt;
        while (DateTimeOffset.UtcNow < deadline)
        {
            Assert.True(NewCache().TryGetSchema(connection.Id, null, out _, out var at));
            cachedAt = at!.Value;
            if (cachedAt > seededAt) break;
            await Task.Delay(100);
        }

        Assert.True(cachedAt > seededAt);
    }

    private JsonDatabaseReadCache NewCache() => new(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A file still open on a slow filesystem is not a test failure.
        }
    }
}
