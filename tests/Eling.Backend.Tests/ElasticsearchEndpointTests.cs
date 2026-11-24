using System.Net;
using System.Text;
using System.Text.Json;
using Eling.Backend.Endpoints;
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
/// The six /api/databases/{id}/es routes, against a stubbed read service.
/// <para>
/// The routes are the only HTTP surface over the read service, so the tests
/// pin: the refusal shapes every route gives a non-ES connection, the clamp
/// rules (size 1-100, from &gt;= 0, from + size &lt;= 10,000), the audit
/// envelope, and the exception-to-status mapping (400/404/502/503). No network:
/// the stub answers everything.
/// </para>
/// </summary>
public sealed class ElasticsearchEndpointTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-es-endpoints-" + Guid.NewGuid().ToString("N"));

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
        // The database route table binds the Redis pool and the ES client cache
        // as handler parameters; a host without them cannot build it at all.
        builder.Services.AddSingleton<IRedisConnectionPool>(new RedisConnectionPool());
        builder.Services.AddSingleton<IRedisReadCache>(sp => new JsonRedisReadCache(
            _root,
            sp.GetRequiredService<ILogger<JsonRedisReadCache>>()));
        builder.Services.AddSingleton<IElasticsearchClientCache>(new ElasticsearchClientCache(TimeSpan.FromDays(30)));
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
    public async Task Indices_ForAnUnknownId_AnswersNotFound()
    {
        var response = await _client.GetAsync("/api/databases/nope/es/indices");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Indices_ForASqliteConnection_NamesTheProvider()
    {
        var id = await RegisterConnection("local.sqlite", DatabaseProvider.Sqlite, "Data Source=local.db");

        var response = await _client.GetAsync($"/api/databases/{id}/es/indices");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sqlite", body);
        Assert.Contains("requires Elasticsearch", body);
    }

    [Fact]
    public async Task Indices_IncludeSystemFalse_IsCapturedByTheService()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        _es.Indices =
        [
            new ElasticsearchIndexInfo("logs-app-2026", "green", "open", 100, 10, 1, 1),
        ];

        var response = await _client.GetAsync($"/api/databases/{id}/es/indices?includeSystem=false");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(_es.IncludeSystem);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("logs-app-2026", body);
    }

    [Fact]
    public async Task Documents_DatelessIndexWithoutRangeOrField_PassesNullFieldToTheService()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync($"/api/databases/{id}/es/documents?index=logs-app-2026");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_es.DocumentsField);
        Assert.Equal("logs-app-2026", _es.DocumentsIndex);
    }

    [Fact]
    public async Task Documents_WithStartAndADatelessStub_AnswersBadRequest()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        _es.Error = new ElasticsearchApiException(
            400,
            "field '@timestamp' in index 'logs-app-2026' is not mapped; it must map to a date");

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/documents?index=logs-app-2026&start=2026-11-01T00:00:00%2B00:00");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("@timestamp", body);
    }

    [Fact]
    public async Task Documents_FiltersWithBadJson_AnswersBadRequest()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/documents?index=logs-app-2026&filters={Uri.EscapeDataString("not json")}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Documents_FiltersWithBadOp_AnswersBadRequest()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        var filters = Uri.EscapeDataString("""[{"field":"level","op":"bogus","value":"error"}]""");

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/documents?index=logs-app-2026&filters={filters}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Documents_FromPlusSizeExactlyTenThousand_IsAllowed()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/documents?index=logs-app-2026&from=9999&size=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(9999, _es.DocumentsFrom);
        Assert.Equal(1, _es.DocumentsSize);
    }

    [Fact]
    public async Task Documents_FromPlusSizeOverTenThousand_AnswersBadRequest()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/documents?index=logs-app-2026&from=10000&size=1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Documents_SizeClampedToHundredBeforeTheWindowCheck_AnswersBadRequest()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/documents?index=logs-app-2026&from=10000&size=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Documents_NegativeFrom_AnswersBadRequest()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/documents?index=logs-app-2026&from=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Documents_SizeClampedToOneHundred_IsCapturedByTheService()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/documents?index=logs-app-2026&size=250");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100, _es.DocumentsSize);
    }

    [Fact]
    public async Task Histogram_DatelessStub_AlwaysAnswersBadRequest()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        _es.Error = new ElasticsearchApiException(
            400,
            "field '@timestamp' in index 'logs-app-2026' is not mapped; it must map to a date");

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/histogram?index=logs-app-2026");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Histogram_BucketsClampedToOneHundredAndDefaultSixty()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var capped = await _client.GetAsync(
            $"/api/databases/{id}/es/histogram?index=logs-app-2026&buckets=250");
        Assert.Equal(HttpStatusCode.OK, capped.StatusCode);
        Assert.Equal(100, _es.HistogramBucketsRequested);

        var defaulted = await _client.GetAsync(
            $"/api/databases/{id}/es/histogram?index=logs-app-2026");
        Assert.Equal(HttpStatusCode.OK, defaulted.StatusCode);
        Assert.Equal(60, _es.HistogramBucketsRequested);
    }

    [Fact]
    public async Task Histogram_ReturnsFieldIntervalAndBucketsAscending()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        _es.HistogramBuckets =
        [
            new ElasticsearchHistogramBucket(1760086400000L, 3855) { Interval = "1d" },
            new ElasticsearchHistogramBucket(1760000000000L, 1204) { Interval = "1d" },
        ];

        var response = await _client.GetAsync(
            $"/api/databases/{id}/es/histogram?index=logs-app-2026&field=@timestamp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("@timestamp", document.RootElement.GetProperty("field").GetString());
        Assert.Equal("1d", document.RootElement.GetProperty("interval").GetString());
        var buckets = document.RootElement.GetProperty("buckets");
        Assert.Equal(2, buckets.GetArrayLength());
        Assert.True(buckets[0].GetProperty("keyMs").GetInt64()
                    < buckets[1].GetProperty("keyMs").GetInt64());
    }

    [Fact]
    public async Task Search_WithAggs_ReturnsAggregationsVerbatim()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        using var aggs = JsonDocument.Parse("""{"by_level":{"buckets":[{"key":"error","doc_count":812}]}}""");
        _es.SearchResult = new ElasticsearchSearchResult(812, 9, Array.Empty<ElasticsearchHit>(), aggs.RootElement.Clone());

        var response = await _client.PostAsync(
            $"/api/databases/{id}/es/search",
            Json("""{"index":"logs-app-2026","query":{"match_all":{}},"aggs":{"by_level":{"terms":{"field":"level"}}},"size":250}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100, _es.SearchSize);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("by_level", body);
        Assert.Contains("error", body);
    }

    [Fact]
    public async Task Search_FromPlusSizeOverWindow_AnswersBadRequest()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.PostAsync(
            $"/api/databases/{id}/es/search",
            Json("""{"index":"logs-app-2026","size":1,"from":10000}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_WithoutQuery_CallsTheServiceWithANullQuery()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.PostAsync(
            $"/api/databases/{id}/es/search",
            Json("""{"index":"logs-app-2026"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_es.SearchQuery);
    }

    [Fact]
    public async Task Count_ReturnsCountAndTookMs()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        _es.CountResult = 18233;

        var response = await _client.PostAsync(
            $"/api/databases/{id}/es/count",
            Json("""{"index":"logs-*"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(18233, document.RootElement.GetProperty("count").GetInt64());
        Assert.True(document.RootElement.TryGetProperty("tookMs", out _));
        Assert.Equal("logs-*", _es.CountIndex);
    }

    [Fact]
    public async Task UnavailableCluster_Answers503()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        _es.Error = new ElasticsearchUnavailableException("connection refused");

        var response = await _client.GetAsync($"/api/databases/{id}/es/indices");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task UnauthenticatedCluster_Answers502()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        _es.Error = new ElasticsearchUnauthenticatedException("authentication failed (401): invalid credentials");

        var response = await _client.GetAsync($"/api/databases/{id}/es/indices");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task ForbiddenCluster_Answers502With403DetailAndNoAccountName()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        _es.Error = new ElasticsearchUnauthenticatedException(403,
            "authentication failed (403): action [indices:monitor/stats] is unauthorized for user [bbone.recdriver]");

        var response = await _client.GetAsync($"/api/databases/{id}/es/indices");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("(403)", body);
        Assert.DoesNotContain("bbone.recdriver", body);
    }

    [Fact]
    public async Task IndexDetail_ForAMissingIndex_Answers404()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");
        _es.Error = new ElasticsearchApiException(404, "index_not_found_exception");

        var response = await _client.GetAsync($"/api/databases/{id}/es/index?index=nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task IndexDetail_ComposesMappingsSettingsAndStats()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.GetAsync($"/api/databases/{id}/es/index?index=logs-app-2026");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("logs-app-2026", document.RootElement.GetProperty("name").GetString());
        Assert.True(document.RootElement.TryGetProperty("mappings", out _));
        Assert.True(document.RootElement.TryGetProperty("settings", out _));
        Assert.Equal("logs-app-2026", _es.DetailIndex);
    }

    [Fact]
    public async Task TestStoredConnection_WithAnEsConnection_ReportsTheClusterVersionAndMarksConnected()
    {
        var id = await RegisterConnection("es@local", DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        var response = await _client.PostAsync($"/api/databases/{id}/test", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("8.19.2", document.RootElement.GetProperty("message").GetString());
        var stored = await _store.FindAsync(id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.NotNull(stored!.LastConnectedAt);
        Assert.Equal(1, _es.PingCalls);
    }

    [Fact]
    public async Task TestFormConnection_WithProviderElasticsearch_ReportsTheVersionWithoutTouchingTheAdapter()
    {
        var response = await _client.PostAsync(
            "/api/databases/test-connection",
            Json("""{"name":"es@local","provider":"Elasticsearch","environment":"Development","allowAgentMutations":false,"requireApprovalForMutations":false,"manual":{"host":"127.0.0.1","port":9200}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("8.19.2", document.RootElement.GetProperty("message").GetString());
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
}
