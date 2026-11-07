using System.Text.Json;

using Eling.Core.Database;
using Eling.Core.Serialization;

namespace Eling.Core.Tests;

/// <summary>
/// The SQL read cache keeps schema and query answers in two files. Forgetting
/// a connection has to forget it in both, and it has to do so under one lock:
/// with two locks the operation could finish half-way and leave the query file
/// still holding entries for a connection that no longer exists.
///
/// These tests pin that behaviour, and the second one pins the other half of
/// the same rule — that two cache objects over the same directory share the
/// lock rather than each building its own.
/// </summary>
public sealed class JsonDatabaseReadCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eling-sql-cache-tests", Guid.NewGuid().ToString("N"));

    private JsonDatabaseReadCache NewCache() => new(_dir);

    private static DatabaseSchemaInfo Schema() => new([]);

    private static QueryResult Result() => new(["id"], [new object?[] { 1 }], false);

    [Fact]
    public void SetAndGet_RoundTripSchemaAndQuery()
    {
        var cache = NewCache();

        cache.SetSchema("c1", "db", Schema());
        cache.SetQuery("c1", "db", 100, "SELECT 1", Result());

        Assert.True(cache.TryGetSchema("c1", "db", out var schema, out var schemaAt));
        Assert.NotNull(schema);
        Assert.NotNull(schemaAt);
        Assert.True(cache.TryGetQuery("c1", "db", 100, "SELECT 1", out var query, out var queryAt));
        Assert.NotNull(query);
        Assert.NotNull(queryAt);
    }

    [Fact]
    public void InvalidateConnection_ClearsBothFiles()
    {
        var cache = NewCache();
        cache.SetSchema("c1", "db", Schema());
        cache.SetQuery("c1", "db", 100, "SELECT 1", Result());
        cache.SetSchema("c2", "db", Schema());
        cache.SetQuery("c2", "db", 100, "SELECT 1", Result());

        cache.InvalidateConnection("c1");

        Assert.False(cache.TryGetSchema("c1", "db", out _, out _));
        Assert.False(cache.TryGetQuery("c1", "db", 100, "SELECT 1", out _, out _));
        Assert.True(cache.TryGetSchema("c2", "db", out _, out _));
        Assert.True(cache.TryGetQuery("c2", "db", 100, "SELECT 1", out _, out _));
    }

    [Fact]
    public void TwoCachesOverTheSameDirectory_SeeEachOthersWrites()
    {
        // Two cache objects over one directory stand in for the dashboard and
        // the MCP host, which each build their own. They must read and write
        // the same files, and must not corrupt each other's lock.
        var first = NewCache();
        var second = NewCache();

        first.SetSchema("c1", "db", Schema());
        second.SetQuery("c1", "db", 100, "SELECT 1", Result());

        Assert.True(second.TryGetSchema("c1", "db", out _, out _));
        Assert.True(first.TryGetQuery("c1", "db", 100, "SELECT 1", out _, out _));

        second.InvalidateConnection("c1");

        Assert.False(first.TryGetSchema("c1", "db", out _, out _));
        Assert.False(first.TryGetQuery("c1", "db", 100, "SELECT 1", out _, out _));
    }

    [Fact]
    public void AnUnchangedEntry_StillGetsAFreshTimestamp()
    {
        // The dashboard polls, so the same schema is written again and again.
        // Skipping the write entirely would freeze the timestamp, and the
        // frozen timestamp is what a reader uses to decide the snapshot is
        // still current - the entry would read as stale forever, and the
        // dashboard would retry against a database that answered fine.
        var cache = NewCache();
        cache.SetSchema("c1", "db", Schema());

        var before = ReadSchemaCachedAt();
        AgeSchemaEntry(TimeSpan.FromMinutes(5));
        cache.SetSchema("c1", "db", Schema());

        Assert.True(ReadSchemaCachedAt() > before);
    }

    [Fact]
    public void AnUnchangedEntry_KeepsItsTimestampWhileItIsStillFresh()
    {
        // While the entry is inside the freshness window, the file already
        // says what a write would say, so the write is skipped - and the
        // timestamp must not move, or the file would be rewritten on every
        // read of every connection.
        var cache = NewCache();
        cache.SetSchema("c1", "db", Schema());

        var first = ReadSchemaCachedAt();
        Thread.Sleep(50);
        cache.SetSchema("c1", "db", Schema());

        Assert.Equal(first, ReadSchemaCachedAt());
    }

    private DateTimeOffset ReadSchemaCachedAt()
    {
        var path = Path.Combine(_dir, "database-schema-cache.json");
        var file = JsonSerializer.Deserialize<DatabaseSchemaCacheFile>(
            File.ReadAllText(path), JsonDefaults.Shared)!;
        return file.Schemas[0].CachedAt;
    }

    /// <summary>
    /// Rewrite the cache file's timestamp, so the age rule is what is under
    /// test rather than the writer.
    /// </summary>
    private void AgeSchemaEntry(TimeSpan age)
    {
        var path = Path.Combine(_dir, "database-schema-cache.json");
        var file = JsonSerializer.Deserialize<DatabaseSchemaCacheFile>(
            File.ReadAllText(path), JsonDefaults.Shared)!;
        var aged = file with
        {
            Schemas = [.. file.Schemas.Select(s => s with { CachedAt = DateTimeOffset.UtcNow - age })]
        };
        File.WriteAllText(path, JsonSerializer.Serialize(aged, JsonDefaults.Shared));
    }

    [Fact]
    public void EntriesOlderThanTheMaxAge_AreNotServed()
    {
        var cache = NewCache();
        cache.SetSchema("c1", "db", Schema());

        // The cache file is written by hand with an old timestamp: the age
        // rule is what is under test, not the writer.
        var path = Path.Combine(_dir, "database-schema-cache.json");
        var file = JsonSerializer.Deserialize<DatabaseSchemaCacheFile>(
            File.ReadAllText(path), JsonDefaults.Shared)!;
        var aged = file with
        {
            Schemas = [file.Schemas[0] with { CachedAt = DateTimeOffset.UtcNow.AddDays(-30) }]
        };
        File.WriteAllText(path, JsonSerializer.Serialize(aged, JsonDefaults.Shared));

        Assert.False(cache.TryGetSchema("c1", "db", out _, out _));
    }

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
}
