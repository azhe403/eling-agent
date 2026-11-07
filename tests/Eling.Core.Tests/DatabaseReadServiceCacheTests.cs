using System.Text.Json;

using Eling.Core.Database;
using Eling.Core.Serialization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Core.Tests;

/// <summary>
/// The read path is cache-first: a snapshot on file answers at once, whatever
/// its age, and says so. The real answer is a separate, cache-bypassing read,
/// so a reachable database is never described as anything but live and a
/// snapshot never passes itself off as current.
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

    /// <summary>A real SQLite database with one table, and a connection to it.</summary>
    private async Task<DatabaseConnection> HealthyConnectionAsync(string table = "users")
    {
        var db = Path.Combine(_dir, $"healthy-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(_dir);
        await using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE TABLE {table} (id INTEGER PRIMARY KEY, name TEXT);";
            await cmd.ExecuteNonQueryAsync();
        }

        return DatabaseConnection.Create(
            "healthy@local",
            DatabaseProvider.Sqlite,
            $"Data Source={db}",
            DatabaseEnvironment.ReadOnly);
    }

    [Fact]
    public async Task ASnapshot_AnswersAsCached_WithoutTouchingTheDatabase()
    {
        // The snapshot answers, whatever its age. The database is not read, so
        // the tables are on screen the moment the cache is - and the answer
        // says it came from the cache, so the caller knows to ask again.
        var connection = BrokenConnection();
        SeedSchema(connection, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5), Schema("users"));

        var result = await NewService().GetSchemaAsync(connection, null);

        Assert.Equal(CacheSources.Cached, result.Source);
        Assert.True(result.FromCache);
        Assert.Equal("users", Assert.Single(result.Tables).Name);
    }

    [Fact]
    public async Task AnEmptyCache_OnAHealthyDatabase_AnswersAsLive()
    {
        var connection = await HealthyConnectionAsync();

        var result = await NewService().GetSchemaAsync(connection, null);

        Assert.Equal(CacheSources.Live, result.Source);
        Assert.False(result.FromCache);
        Assert.Equal("users", Assert.Single(result.Tables).Name);
    }

    [Fact]
    public async Task AnEmptyCache_OnAnUnreachableDatabase_Throws()
    {
        // There is nothing to answer with: the snapshot is what the cache is
        // for, and there is no snapshot.
        var connection = BrokenConnection();

        var thrown = await Record.ExceptionAsync(() => NewService().GetSchemaAsync(connection, null));

        Assert.IsType<DatabaseUnavailableException>(thrown);
    }

    [Fact]
    public async Task PreferCacheFalse_ReadsTheDatabase_EvenWhenASnapshotExists()
    {
        // The cache-bypassing read is the revalidate: it reaches the database,
        // so a stale snapshot is replaced by the real answer.
        var connection = await HealthyConnectionAsync();
        SeedSchema(connection, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5), Schema("stale_table"));

        var result = await NewService().GetSchemaAsync(connection, null, preferCache: false);

        Assert.Equal(CacheSources.Live, result.Source);
        Assert.False(result.FromCache);
        // The database was read, so the snapshot's stale table is gone.
        Assert.Equal("users", Assert.Single(result.Tables).Name);
    }

    [Fact]
    public async Task PreferCacheFalse_OnAnUnreachableDatabase_FallsBackToTheSnapshot()
    {
        // A revalidate asks for the database. It cannot be reached, so the
        // snapshot answers - but it says so, and it is not passed off as live.
        var connection = BrokenConnection();
        SeedSchema(connection, DateTimeOffset.UtcNow, Schema("users"));

        var result = await NewService().GetSchemaAsync(connection, null, preferCache: false);

        Assert.Equal(CacheSources.Offline, result.Source);
        Assert.True(result.FromCache);
        Assert.Equal("users", Assert.Single(result.Tables).Name);
    }

    [Fact]
    public async Task AnUnreachableDatabase_WithASnapshot_AnswersOffline()
    {
        // The shape the dashboard actually sees: the tree is open, the
        // database goes away, and the snapshot answers marked offline.
        var connection = await HealthyConnectionAsync();
        await NewService().GetSchemaAsync(connection, null, preferCache: false);

        var broken = connection with { ConnectionString = BrokenConnection().ConnectionString };
        var result = await NewService().GetSchemaAsync(broken, null, preferCache: false);

        Assert.Equal(CacheSources.Offline, result.Source);
        Assert.True(result.FromCache);
        Assert.NotNull(result.CacheTtlSeconds);
    }

    [Fact]
    public async Task ACachedAnswer_CarriesAConsistentTtl()
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
