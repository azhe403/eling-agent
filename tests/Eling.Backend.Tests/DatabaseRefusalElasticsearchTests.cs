using System.Net;
using System.Text;
using System.Text.Json;
using Eling.Backend.Endpoints;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Serialization;

namespace Eling.Backend.Tests;

/// <summary>
/// The refusal matrix at the endpoint boundary: SQL routes refuse an
/// Elasticsearch connection, the /{id}/keys routes refuse one, and every /es
/// route refuses a non-ES connection — each with a message that names the
/// provider or the ES surface it points at.
/// </summary>
public sealed class DatabaseRefusalElasticsearchTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-es-refusals-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private JsonDatabaseConnectionStore _store = null!;
    private StubElasticsearchReadService _es = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new JsonDatabaseConnectionStore(Path.Combine(_root, "databases.json"));
        _es = new StubElasticsearchReadService();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditLogger>(NullAuditLogger.Instance);
        builder.Services.AddSingleton<IDatabaseConnectionStore>(_store);
        builder.Services.AddSingleton<IDatabaseReadCache>(sp => new JsonDatabaseReadCache(
            _root,
            sp.GetRequiredService<ILogger<JsonDatabaseReadCache>>()));
        builder.Services.AddSingleton<IDatabaseReadService>(sp => new DatabaseReadService(
            sp.GetRequiredService<IDatabaseReadCache>(),
            sp.GetRequiredService<ILogger<DatabaseReadService>>()));
        builder.Services.AddSingleton<IElasticsearchReadService>(_es);
        // The route table binds the Redis pool and the ES client cache as
        // handler parameters; a host without them cannot build it.
        builder.Services.AddSingleton<IRedisConnectionPool>(new RedisConnectionPool());
        builder.Services.AddSingleton<IRedisReadCache>(sp => new JsonRedisReadCache(
            _root,
            sp.GetRequiredService<ILogger<JsonRedisReadCache>>()));
        builder.Services.AddSingleton<IElasticsearchClientCache>(new ElasticsearchClientCache(TimeSpan.FromDays(30)));
        builder.Services.AddSingleton<IRedisKeyReadService>(new StubRedisKeyReadService());
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
        _app.MapDatabaseElasticsearchEndpoints();
        _app.MapDatabaseKeyEndpoints();
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

    [Fact]
    public async Task Schema_ForAnEsConnection_AnswersBadRequestNamingElasticsearch()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync($"/api/databases/{id}/schema");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Elasticsearch", body);
        Assert.Contains("/es/", body);
    }

    [Fact]
    public async Task Query_ForAnEsConnection_AnswersBadRequestNamingElasticsearch()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.PostAsync(
            $"/api/databases/{id}/query",
            Json("""{"sql":"SELECT 1"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Elasticsearch", body);
        Assert.Contains("/es/", body);
    }

    [Fact]
    public async Task Browse_ForAnEsConnection_AnswersBadRequestNamingElasticsearch()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.PostAsync(
            $"/api/databases/{id}/browse",
            Json("""{"table":"logs","page":1,"limit":25}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Elasticsearch", body);
        Assert.Contains("/es/", body);
    }

    [Fact]
    public async Task Mutate_ForAnEsConnection_AnswersBadRequestReadOnly()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.PostAsync(
            $"/api/databases/{id}/mutate",
            Json("""{"sql":"DELETE FROM logs"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Elasticsearch", body);
        Assert.Contains("read-only", body);
    }

    [Fact]
    public async Task ListDatabases_WithAnEsProvider_AnswersBadRequestNamingElasticsearch()
    {
        var response = await _client.PostAsync(
            "/api/databases/list-databases",
            Json("""{"name":"es@local","provider":"Elasticsearch","environment":"Development","allowAgentMutations":false,"requireApprovalForMutations":false,"connectionString":"http://127.0.0.1:9200/"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Elasticsearch", body);
    }

    [Fact]
    public async Task Keys_ForAnEsConnection_AnswersBadRequestPointingAtEs()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync($"/api/databases/{id}/keys");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Elasticsearch", body);
        Assert.Contains("es_mapping", body);
    }

    [Fact]
    public async Task KeysDatabases_ForAnEsConnection_AnswersBadRequestPointingAtEs()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/databases");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Elasticsearch", body);
        Assert.Contains("es_mapping", body);
    }

    [Theory]
    [InlineData("es/indices")]
    [InlineData("es/index?index=logs-app-2026")]
    [InlineData("es/documents?index=logs-app-2026")]
    [InlineData("es/histogram?index=logs-app-2026")]
    public async Task EsGetRoutes_ForASqliteConnection_NameTheProvider(string path)
    {
        var id = await RegisterConnection("local.sqlite", DatabaseProvider.Sqlite, "Data Source=local.db");

        var response = await _client.GetAsync($"/api/databases/{id}/{path}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sqlite", body);
        Assert.Contains("requires Elasticsearch", body);
    }

    [Fact]
    public async Task EsSearch_ForASqliteConnection_AnswersBadRequestPointingAtEsSearchAndEsMapping()
    {
        var id = await RegisterConnection("local.sqlite", DatabaseProvider.Sqlite, "Data Source=local.db");

        var response = await _client.PostAsync(
            $"/api/databases/{id}/es/search",
            Json("""{"index":"logs-app-2026"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("es_search", body);
        Assert.Contains("es_mapping", body);
    }

    [Fact]
    public async Task EsCount_ForASqliteConnection_AnswersBadRequestPointingAtEsSearchAndEsMapping()
    {
        var id = await RegisterConnection("local.sqlite", DatabaseProvider.Sqlite, "Data Source=local.db");

        var response = await _client.PostAsync(
            $"/api/databases/{id}/es/count",
            Json("""{"index":"logs-app-2026"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("es_search", body);
        Assert.Contains("es_mapping", body);
    }

    [Fact]
    public async Task EsIndices_ForAPostgresConnection_NamesTheProvider()
    {
        var id = await RegisterConnection("pg@local", DatabaseProvider.PostgreSql, "Host=db;Username=agent;Password=secret;Database=orders");

        var response = await _client.GetAsync($"/api/databases/{id}/es/indices");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PostgreSql", body);
        Assert.Contains("requires Elasticsearch", body);
    }

    [Fact]
    public async Task EsIndices_ForARedisConnection_NamesTheProvider()
    {
        var id = await RegisterConnection("redis@local", DatabaseProvider.Redis, "redis.internal:6379");

        var response = await _client.GetAsync($"/api/databases/{id}/es/indices");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Redis", body);
    }

    [Fact]
    public async Task AgentGetSchema_ForAnEsConnection_NamesElasticsearchAndTheEsTools()
    {
        // The MCP tool path: db_get_schema on an ES connection must refuse with
        // the provider's own words, not fall through to a generic
        // "Provider 'Elasticsearch' is not supported."
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var tool = new DatabaseGetSchemaTool(
            _store,
            new DatabaseReadService(new JsonDatabaseReadCache(Path.Combine(_root, "cache"))),
            NullLogger<DatabaseGetSchemaTool>.Instance);

        var thrown = await Record.ExceptionAsync(() => tool.GetSchemaAsync(id));

        var error = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains("Elasticsearch", error.Message);
        Assert.Contains("es_mapping", error.Message);
        Assert.Contains("es_search", error.Message);
    }

    private async Task<string> RegisterConnection(string name, DatabaseProvider provider, string connectionString)
    {
        var connection = DatabaseConnection.Create(
            name,
            provider,
            connectionString,
            DatabaseEnvironment.Development);
        await _store.UpsertAsync(connection, CancellationToken.None);
        return connection.Id;
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    /// <summary>
    /// The key routes bind the Redis read service as a handler parameter; this
    /// test is about the refusals, so the service is never expected to run.
    /// </summary>
    private sealed class StubRedisKeyReadService : IRedisKeyReadService
    {
        public Task<IReadOnlyList<RedisDatabaseInfo>> ListDatabasesAsync(
            DatabaseConnection connection, CancellationToken cancellationToken = default, bool preferCache = true)
            => throw new NotImplementedException("the key browsing service is not part of this test.");

        public Task<KeyPage> ScanAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string prefix,
            long cursor,
            int pageOffset,
            int pageSize,
            CancellationToken cancellationToken = default,
            bool preferCache = true)
            => throw new NotImplementedException("the key browsing service is not part of this test.");

        public Task<KeyInspectResult> InspectAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string key,
            CancellationToken cancellationToken = default, bool preferCache = true)
            => throw new NotImplementedException("the key browsing service is not part of this test.");

        public Task<RedisKeyProbeResult> ProbeKeyAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string key,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("the key browsing service is not part of this test.");

        public Task<IReadOnlyList<RedisKeyProbeResult>> ProbeKeysAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("the key browsing service is not part of this test.");

        public Task<RedisCommandResult> ExecuteCommandAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string command,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("the key browsing service is not part of this test.");

        public Task<RedisKeyDeleteResult> DeleteKeyAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string key,
            CancellationToken ct = default)
            => throw new NotImplementedException("the key browsing service is not part of this test.");

        public Task<RedisPrefixPreviewResult> PreviewPrefixAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string prefix,
            CancellationToken ct = default)
            => throw new NotImplementedException("the key browsing service is not part of this test.");

        public Task<RedisPrefixDeleteResult> DeletePrefixAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string prefix,
            CancellationToken ct = default)
            => throw new NotImplementedException("the key browsing service is not part of this test.");
    }
}
