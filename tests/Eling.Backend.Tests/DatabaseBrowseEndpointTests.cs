using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend.Endpoints;
using Eling.Core.Audit;
using Eling.Core.Database;
using Eling.Core.Database.Browse;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// The structured read path the Data tab uses: a page of rows plus the total
/// behind it, with the condition folded in server-side. SQLite is the honest
/// database here - the adapter opens a real file, the schema read is real,
/// and only the offline-cache tests break the connection on purpose.
/// </summary>
public sealed class DatabaseBrowseEndpointTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-db-browse-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private JsonDatabaseConnectionStore _store = null!;
    private RecordingAuditLogger _audit = null!;

    /// <summary>Records the audit events so a test can assert on them.</summary>
    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public readonly List<AuditEvent> Entries = [];
        public ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default)
        {
            lock (Entries) Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new JsonDatabaseConnectionStore(Path.Combine(_root, "databases.json"));
        _audit = new RecordingAuditLogger();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditLogger>(_audit);
        builder.Services.AddSingleton<IDatabaseConnectionStore>(_store);
        builder.Services.AddSingleton<IDatabaseReadCache>(sp => new JsonDatabaseReadCache(
            _root,
            sp.GetRequiredService<ILogger<JsonDatabaseReadCache>>()));
        builder.Services.AddSingleton<IDatabaseReadService>(sp => new DatabaseReadService(
            sp.GetRequiredService<IDatabaseReadCache>(),
            sp.GetRequiredService<ILogger<DatabaseReadService>>()));
        builder.Services.AddSingleton<IRedisConnectionPool>(new RedisConnectionPool());
        builder.Services.AddSingleton<IRedisReadCache>(sp => new JsonRedisReadCache(
            _root,
            sp.GetRequiredService<ILogger<JsonRedisReadCache>>()));
        var scanner = new AppSettingsConnectionScanner(_root);
        builder.Services.AddSingleton(scanner);
        builder.Services.AddSingleton(new Eling.Backend.Database.DatabaseConnectionImporter(scanner, _store));
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.MapDatabaseEndpoints();
        await _app.StartAsync();
        _client = _app.CreateEnvelopeClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Creates a SQLite file with the seeded table and registers a read-only connection to it.</summary>
    private async Task<string> SeedSqliteAsync(string name = "browse@test")
    {
        var db = Path.Combine(_root, name + ".db");
        await using (var conn = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE mst_test (id INTEGER PRIMARY KEY, name TEXT NOT NULL, is_active TINYINT(1) NOT NULL DEFAULT 1, updated_at TEXT NOT NULL);"
                + "INSERT INTO mst_test (id, name, is_active, updated_at) VALUES"
                + "(1, 'Alpha', 1, '2025-02-19 10:00:00'),"
                + "(2, 'Beta', 0, '2025-02-18 10:00:00'),"
                + "(3, 'alphabet soup', 1, '2025-02-20 10:00:00'),"
                + "(4, 'Zulu', 1, '2025-02-17 10:00:00');";
            await cmd.ExecuteNonQueryAsync();
        }

        var connection = DatabaseConnection.Create(
            name, DatabaseProvider.Sqlite, $"Data Source={db}", DatabaseEnvironment.ReadOnly);
        await _store.UpsertAsync(connection);
        return connection.Id;
    }

    private async Task<HttpResponseMessage> PostBrowse(string id, object body)
        => await _client.PostAsync("/api/databases/" + id + "/browse",
            new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));

    /// <summary>Seeds a REAL column - the case Microsoft.Data.Sqlite binds as TEXT.</summary>
    private async Task<string> SeedRealScoreAsync(string name)
    {
        var db = Path.Combine(_root, name + ".db");
        await using (var conn = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE scores (id INTEGER PRIMARY KEY, name TEXT, score REAL NOT NULL DEFAULT 0);"
                + "INSERT INTO scores (name, score) VALUES ('low', 1.5), ('mid', 2.5), ('high', 10.25);";
            await cmd.ExecuteNonQueryAsync();
        }

        var connection = DatabaseConnection.Create(
            name, DatabaseProvider.Sqlite, $"Data Source={db}", DatabaseEnvironment.ReadOnly);
        await _store.UpsertAsync(connection);
        return connection.Id;
    }

    [Fact]
    public async Task Browse_HappyPath_ReturnsRowsAndTotal()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new
        {
            table = "mst_test",
            page = 1,
            limit = 50,
            filters = new[] { new { column = "name", op = "contains", value = "a", enabled = true, logic = "first" } },
            sort = new[] { new { column = "updated_at", order = "desc" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("total").GetInt64());
        Assert.False(root.GetProperty("fromCache").GetBoolean());
        var ids = root.GetProperty("rows").EnumerateArray().Select(r => r[0].GetInt64()).ToList();
        Assert.Equal(new long[] { 3, 1, 2 }, ids);
        Assert.Equal(new string[] { "id", "name", "is_active", "updated_at" },
            root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
    }

    [Fact]
    public async Task Browse_Unfiltered_ReturnsAllRowsAndCorrectTotal()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new { table = "mst_test", page = 1, limit = 50 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(4, doc.RootElement.GetProperty("total").GetInt64());
        Assert.Equal(4, doc.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Browse_SortAscendingDescending_ReturnsSortedOrder()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new
        {
            table = "mst_test",
            sort = new[] { new { column = "name", order = "desc" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => r[1].GetString()).ToArray();
        Assert.Equal(new[] { "alphabet soup", "Zulu", "Beta", "Alpha" }, names);
    }

    [Fact]
    public async Task Browse_NumericFilterGt_OnRealColumn_ReturnsMatchingRows()
    {
        var id = await SeedRealScoreAsync("scores-gt@test");

        var response = await PostBrowse(id, new
        {
            table = "scores",
            filters = new[] { new { column = "score", op = "gt", value = "2", enabled = true, logic = "first" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt64());
        var names = doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => r[1].GetString()).ToArray();
        Assert.Equal(new[] { "mid", "high" }, names);
    }

    [Fact]
    public async Task Browse_NumericFilterEq_OnRealColumn_ReturnsTheOneRow()
    {
        var id = await SeedRealScoreAsync("scores-eq@test");

        var response = await PostBrowse(id, new
        {
            table = "scores",
            filters = new[] { new { column = "score", op = "eq", value = "2.5", enabled = true, logic = "first" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Browse_UnknownTable_Answers400WithTableName()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new { table = "nope" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nope", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Browse_UnknownColumn_Answers400WithColumnName()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new
        {
            table = "mst_test",
            filters = new[] { new { column = "nope", op = "eq", value = "x", enabled = true, logic = "first" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nope", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Browse_InvalidNumericValue_Answers400()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new
        {
            table = "mst_test",
            filters = new[] { new { column = "is_active", op = "eq", value = "banana", enabled = true, logic = "first" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is_active", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Browse_TinyIntTrueValue_IsAccepted()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new
        {
            table = "mst_test",
            filters = new[] { new { column = "is_active", op = "eq", value = "true", enabled = true, logic = "first" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Browse_InvalidOp_Answers400()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new
        {
            table = "mst_test",
            filters = new[] { new { column = "name", op = "like", value = "x", enabled = true, logic = "first" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("like", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Browse_InvalidOrderValue_Answers400()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new
        {
            table = "mst_test",
            sort = new[] { new { column = "name", order = "sideways" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("sideways", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Browse_DisabledRowWithUnknownColumn_AnswersOk()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new
        {
            table = "mst_test",
            filters = new[] { new { column = "nope", op = "eq", value = "x", enabled = false, logic = "first" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Browse_RedisConnection_Answers400()
    {
        var connection = DatabaseConnection.Create(
            "redis@test", DatabaseProvider.Redis, "localhost:6379", DatabaseEnvironment.ReadOnly);
        await _store.UpsertAsync(connection);

        var response = await PostBrowse(connection.Id, new { table = "mst_test" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Browse_OfflineWithPrimedCache_AnswersFromCache()
    {
        var connection = DatabaseConnection.Create(
            "broken@test",
            DatabaseProvider.MySql,
            "Server=127.0.0.1;Port=1;Database=broken;User Id=u;Password=p;ConnectionTimeout=1",
            DatabaseEnvironment.ReadOnly);
        await _store.UpsertAsync(connection);

        var cache = new JsonDatabaseReadCache(_root, NullLogger<JsonDatabaseReadCache>.Instance);
        var schema = new DatabaseSchemaInfo(
        [
            new TableSchema("mst_test", "table",
                [new ColumnSchema("name", "varchar(50)", true, false), new ColumnSchema("updated_at", "datetime", true, false)],
                [], []),
        ]);
        cache.SetSchema(connection.Id, null, schema);

        var plan = BrowseQueryBuilder.Build(
            DatabaseProvider.MySql,
            "mst_test",
            [new BrowseFilter("name", "eq", "a", true, "first")],
            [new BrowseSort("updated_at", "asc")],
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "varchar(50)",
                ["updated_at"] = "datetime",
            },
            schemaAvailable: true,
            page: 1,
            limit: 50);

        cache.SetQuery(
            connection.Id, null, 50,
            QueryCacheKey.For(plan.Rows.Sql, plan.Rows.Parameters),
            new QueryResult(["id", "name"], [[1L, "a"]], false));
        cache.SetQuery(
            connection.Id, null, 1,
            QueryCacheKey.For(plan.Count.Sql, plan.Count.Parameters),
            new QueryResult(["COUNT(*)"], [[1L]], false));

        var response = await PostBrowse(connection.Id, new
        {
            table = "mst_test",
            filters = new[] { new { column = "name", op = "eq", value = "a", enabled = true, logic = "first" } },
            sort = new[] { new { column = "updated_at", order = "asc" } },
            page = 1,
            limit = 50,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("fromCache").GetBoolean());
        Assert.Equal(1L, doc.RootElement.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Browse_CountMissingFromCache_Answers502()
    {
        var connection = DatabaseConnection.Create(
            "broken@test-502",
            DatabaseProvider.MySql,
            "Server=127.0.0.1;Port=1;Database=broken;User Id=u;Password=p;ConnectionTimeout=1",
            DatabaseEnvironment.ReadOnly);
        await _store.UpsertAsync(connection);

        var cache = new JsonDatabaseReadCache(_root, NullLogger<JsonDatabaseReadCache>.Instance);
        var schema = new DatabaseSchemaInfo(
        [
            new TableSchema("mst_test", "table",
                [new ColumnSchema("name", "varchar(50)", true, false)],
                [], []),
        ]);
        cache.SetSchema(connection.Id, null, schema);

        var plan = BrowseQueryBuilder.Build(
            DatabaseProvider.MySql,
            "mst_test",
            [new BrowseFilter("name", "eq", "a", true, "first")],
            [],
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["name"] = "varchar(50)" },
            schemaAvailable: true,
            page: 1,
            limit: 50);

        // Rows primed, count is not - half a cached answer is worse than 502.
        cache.SetQuery(
            connection.Id, null, 50,
            QueryCacheKey.For(plan.Rows.Sql, plan.Rows.Parameters),
            new QueryResult(["id", "name"], [[1L, "a"]], false));

        var response = await PostBrowse(connection.Id, new
        {
            table = "mst_test",
            filters = new[] { new { column = "name", op = "eq", value = "a", enabled = true, logic = "first" } },
            page = 1,
            limit = 50,
        });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Browse_ReadOnlyEnvironment_IsAllowed()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new { table = "mst_test", limit = 10 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Browse_AuditActionIsDbBrowse()
    {
        var id = await SeedSqliteAsync();

        var response = await PostBrowse(id, new { table = "mst_test", limit = 10 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AuditEvent? entry;
        lock (_audit.Entries)
        {
            entry = _audit.Entries.LastOrDefault(e => e.Action == "db.browse");
        }
        Assert.NotNull(entry);
        Assert.Equal(AuditTrail.Access, entry!.Trail);
        Assert.Equal(AuditCategory.Database, entry.Category);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(4, entry.ResultCount);
        Assert.Contains("mst_test", entry.Query);
    }
}
