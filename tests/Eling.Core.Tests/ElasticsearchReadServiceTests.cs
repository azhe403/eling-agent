using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elastic.Clients.Elasticsearch;
using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// Handler-stub tests for <see cref="ElasticsearchReadService"/>. No test in this
/// file may touch the network: every canned response flows through an in-memory
/// <see cref="HttpMessageHandler"/> that the production client talks to through the
/// transport's <see cref="Elastic.Transport.HttpRequestInvoker"/> seam. The stub
/// decompresses gzip request bodies (the client compresses by default) and records
/// each requested path/query plus the decoded body, so tests can pin what the
/// service sent without mocking inside the client.
/// </summary>
public sealed class ElasticsearchReadServiceTests
{
    private static readonly Uri TestCluster = new("http://elasticsearch-test:9200/");

    [Fact]
    public async Task Ping_ReturnsVersion()
    {
        var (service, handler) = CreateService(_ => JsonResponse(HttpStatusCode.OK,
            """{"name":"n","cluster_name":"c","cluster_uuid":"u","version":{"number":"8.19.2","build_flavor":"default","lucene_version":"9.10.0"},"tagline":"You Know, for Search"}"""));

        var result = await service.PingAsync(MakeConnection(), CancellationToken.None);

        Assert.Equal("8.19.2", result.Version);
        Assert.True(result.LatencyMs >= 0);
        Assert.Equal("GET /", Assert.Single(handler.RequestedPaths));
    }

    [Fact]
    public async Task ListIndices_HidesDotIndicesByDefault()
    {
        var stats = CannedStatsResponse;
        var (service, handler) = CreateService(_ => JsonResponse(HttpStatusCode.OK, stats));

        var visible = await service.ListIndicesAsync(MakeConnection(), includeSystem: false, CancellationToken.None);

        Assert.Equal(
            new[] { "logs-app-2026", "logs-sys-2026" },
            visible.Select(i => i.Name).ToArray());

        var app = visible.Single(i => i.Name == "logs-app-2026");
        Assert.Equal("green", app.Health);
        Assert.Equal("open", app.Status);
        Assert.Equal(124312, app.DocsCount);
        Assert.Equal(48211320, app.StoreSizeBytes);
        Assert.Equal(1, app.PrimaryShards);
        Assert.Equal(1, app.Replicas);
        Assert.Equal("GET /_stats", Assert.Single(handler.RequestedPaths));

        var withSystem = await service.ListIndicesAsync(MakeConnection(), includeSystem: true, CancellationToken.None);
        Assert.Equal(
            new[] { ".kibana", "logs-app-2026", "logs-sys-2026" },
            withSystem.Select(i => i.Name).ToArray());
    }

    [Fact]
    public async Task GetIndexDetail_Missing_Throws404Shaped()
    {
        var (service, handler) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_mapping", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.NotFound,
                    """{"error":{"type":"index_not_found_exception","reason":"no such index [missing-index]","root_cause":[{"type":"index_not_found_exception","reason":"no such index [missing-index]"}]},"status":404}""");
            }

            throw new InvalidOperationException($"unexpected request {path}");
        });

        var exception = await Assert.ThrowsAsync<ElasticsearchApiException>(
            () => service.GetIndexDetailAsync(MakeConnection(), "missing-index", CancellationToken.None));

        Assert.Equal(404, exception.Status);
        Assert.Contains("no such index", exception.Reason);
        // The mapping call is the only HTTP call: a missing index never reaches
        // settings or stats.
        Assert.Equal("GET /missing-index/_mapping", Assert.Single(handler.RequestedPaths));
    }

    [Fact]
    public async Task GetIndexDetail_ReturnsMappingsSettingsAndStats()
    {
        var canned = new Dictionary<string, Func<string>>
        {
            ["mapping"] = () => """{"logs-app-2026":{"mappings":{"properties":{"message":{"type":"text"},"@timestamp":{"type":"date"}}}}}""",
            ["settings"] = () => """{"logs-app-2026":{"settings":{"index":{"number_of_shards":"1","number_of_replicas":"1"}}}}""",
            ["stats"] = () => CannedStatsResponse,
        };
        var (service, handler) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_mapping", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, canned["mapping"]());
            }

            if (path.EndsWith("/_settings", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, canned["settings"]());
            }

            if (path.EndsWith("/_stats", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, canned["stats"]());
            }

            throw new InvalidOperationException($"unexpected request {path}");
        });

        var detail = await service.GetIndexDetailAsync(MakeConnection(), "logs-app-2026", CancellationToken.None);

        Assert.Equal("logs-app-2026", detail.Name);
        Assert.Equal(124312, detail.DocsCount);
        Assert.Equal(48211320, detail.StoreSizeBytes);
        Assert.Contains("properties", detail.Mappings.GetRawText());
        Assert.Contains("number_of_replicas", detail.Settings.GetRawText());
        // The detail pin: one _stats call scoped to this index, not the
        // cluster-wide variant the list endpoint uses.
        Assert.Contains("GET /logs-app-2026/_stats", handler.RequestedPaths);
        Assert.DoesNotContain("GET /_stats", handler.RequestedPaths);
        Assert.Equal(3, handler.RequestedPaths.Count);
    }

    [Fact]
    public async Task Documents_NoRange_DoesNotValidateField()
    {
        var (service, handler) = CreateService(_ => JsonResponse(HttpStatusCode.OK, CannedSearchResponse));

        var page = await service.DocumentsAsync(
            MakeConnection(),
            index: "logs-app-2026",
            from: 0,
            size: 25,
            q: null,
            start: null,
            end: null,
            field: null,
            filters: null,
            logic: null,
            CancellationToken.None);

        // The on-demand rule in the spec: no start/end/field means no mapping
        // validation call and no range clause on the wire.
        var request = Assert.Single(handler.RequestedPaths);
        Assert.Equal("POST /logs-app-2026/_search", request);
        Assert.DoesNotContain("\"range\"", handler.Bodies.Single());
        Assert.Equal(124312, page.Total);
        Assert.Equal(25, page.Size);
        Assert.Equal(0, page.From);
        Assert.Single(page.Hits);
        Assert.Equal("logs-app-2026", page.Hits[0].Index);
        Assert.Equal("abc123", page.Hits[0].Id);
    }

    [Fact]
    public async Task Documents_WithRange_OnDatelessField_Throws400Shaped()
    {
        var (service, handler) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_mapping", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK,
                    """{"logs-app-2026":{"mappings":{"properties":{"level":{"type":"keyword"}}}}}""");
            }

            throw new InvalidOperationException($"unexpected request {path}");
        });

        var exception = await Assert.ThrowsAsync<ElasticsearchApiException>(() => service.DocumentsAsync(
            MakeConnection(),
            index: "logs-app-2026",
            from: 0,
            size: 25,
            q: null,
            start: new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero),
            end: null,
            field: null,
            filters: null,
            logic: null,
            CancellationToken.None));

        Assert.Equal(400, exception.Status);
        Assert.Contains("@timestamp", exception.Reason);
        // Validation short-circuits: mapping is read, the search never happens.
        Assert.Equal("GET /logs-app-2026/_mapping", Assert.Single(handler.RequestedPaths));
    }

    [Fact]
    public async Task Documents_WithExplicitField_ValidatesIt()
    {
        var (service, handler) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_mapping", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK,
                    """{"logs-app-2026":{"mappings":{"properties":{"level":{"type":"keyword"}}}}}""");
            }

            throw new InvalidOperationException($"unexpected request {path}");
        });

        var exception = await Assert.ThrowsAsync<ElasticsearchApiException>(() => service.DocumentsAsync(
            MakeConnection(),
            index: "logs-app-2026",
            from: 0,
            size: 25,
            q: null,
            start: null,
            end: null,
            field: "level",
            filters: null,
            logic: null,
            CancellationToken.None));

        Assert.Equal(400, exception.Status);
        Assert.Contains("level", exception.Reason);
        Assert.Equal("GET /logs-app-2026/_mapping", Assert.Single(handler.RequestedPaths));
    }

    [Fact]
    public async Task Documents_RequestBody_PinsDocOrderAndTrackTotalHits()
    {
        var (service, handler) = CreateService(_ => JsonResponse(HttpStatusCode.OK, CannedSearchResponse));

        await service.DocumentsAsync(
            MakeConnection(),
            index: "logs-app-2026",
            from: 0,
            size: 25,
            q: null,
            start: null,
            end: null,
            field: null,
            filters: null,
            logic: null,
            CancellationToken.None);

        // The documents route pins `_doc` order and track_total_hits on the wire
        // (spec §4, route behavior for /es/documents). The body must round-trip
        // through the ES client's typed SearchRequest (DeserializeBody), which
        // models the _doc sort as FieldSort{Field = "_doc", Order = Asc} — on
        // the wire that is [{"_doc": {"order": "asc"}}].
        var body = JsonNode.Parse(handler.Bodies.Single())!.AsObject();
        Assert.True(body["track_total_hits"]!.GetValue<bool>());
        // The ES .NET client serializes FieldSort{Field = "_doc", Order = Asc}
        // on the wire as {"_doc": {"order": "asc"}}.
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"_doc":{"order":"asc"}}"""),
            body["sort"]!));
    }

    [Fact]
    public async Task Histogram_NonDateField_Throws400Shaped()
    {
        var (service, handler) = CreateService(_ => throw new InvalidOperationException("the handler must not be called"));

        // The filter compiler rejects a range value that is neither numeric nor an
        // ISO date, before any HTTP call is made.
        var exception = await Assert.ThrowsAsync<ElasticsearchApiException>(() => service.HistogramAsync(
            MakeConnection(),
            index: "logs-app-2026",
            q: null,
            start: null,
            end: null,
            field: "@timestamp",
            buckets: 60,
            filters: new[]
            {
                new ElasticsearchFilterRow(Field: "@timestamp", Op: "gte", Value: "not-a-date-value"),
            },
            logic: null,
            CancellationToken.None));

        Assert.Equal(400, exception.Status);
        Assert.Empty(handler.RequestedPaths);
    }

    [Fact]
    public async Task Histogram_KeywordField_Throws400Shaped()
    {
        var (service, handler) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_mapping", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK,
                    """{"logs-app-2026":{"mappings":{"properties":{"@timestamp":{"type":"keyword"}}}}}""");
            }

            throw new InvalidOperationException($"unexpected request {path}");
        });

        var exception = await Assert.ThrowsAsync<ElasticsearchApiException>(() => service.HistogramAsync(
            MakeConnection(),
            index: "logs-app-2026",
            q: null,
            start: null,
            end: null,
            field: "@timestamp",
            buckets: 60,
            filters: null,
            logic: null,
            CancellationToken.None));

        Assert.Equal(400, exception.Status);
        Assert.Contains("@timestamp", exception.Reason);
        // The search never happens: the only call on the wire is the mapping read.
        Assert.Equal("GET /logs-app-2026/_mapping", Assert.Single(handler.RequestedPaths));
    }

    [Fact]
    public async Task Histogram_ReturnsBucketsAscending()
    {
        var canned = """
        {"took":3,"hits":{"total":{"value":99,"relation":"eq"},"hits":[]},"aggregations":{"auto_date_histogram#histogram":{"buckets":[{"key_as_string":"2026-11-18T00:00:00.000Z","key":1763376000000,"doc_count":40},{"key_as_string":"2026-11-19T00:00:00.000Z","key":1763462400000,"doc_count":59}],"interval":"1d"}}}
        """;
        var (service, handler) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_mapping", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK,
                    """{"logs-app-2026":{"mappings":{"properties":{"@timestamp":{"type":"date"}}}}}""");
            }

            return JsonResponse(HttpStatusCode.OK, canned);
        });

        var buckets = await service.HistogramAsync(
            MakeConnection(),
            index: "logs-app-2026",
            q: null,
            start: null,
            end: null,
            field: "@timestamp",
            buckets: 60,
            filters: null,
            logic: null,
            CancellationToken.None);

        Assert.Equal(
            new long[] { 1763376000000, 1763462400000 },
            buckets.Select(b => b.KeyMs).ToArray());
        Assert.Equal(new long[] { 40, 59 }, buckets.Select(b => b.DocCount).ToArray());
        Assert.All(buckets, b => Assert.Equal("1d", b.Interval));
        // typed_keys=true is what makes the typed client able to read the
        // aggregations back: it must be on the wire for the search call.
        var search = handler.RequestedPaths.Last();
        Assert.Equal("POST /logs-app-2026/_search", search.Split('?')[0]);
        Assert.Contains("typed_keys=true", handler.RequestedUris.Last());
    }

    [Fact]
    public async Task Search_WithAggs_ReturnsAggregations()
    {
        // The wire keys come back prefixed with the variant when typed_keys=true;
        // the service strips the prefix so the caller sees exactly what it sent.
        var canned = """
        {"took":9,"hits":{"total":{"value":812,"relation":"eq"},"max_score":null,"hits":[]},"aggregations":{"auto_date_histogram#my_agg":{"buckets":[{"key_as_string":"2026-11-19T00:00:00.000Z","key":1763462400000,"doc_count":42}],"interval":"1d"}}}
        """;
        var (service, handler) = CreateService(_ => JsonResponse(HttpStatusCode.OK, canned));

        var aggs = JsonNode.Parse("""{"my_agg":{"auto_date_histogram":{"field":"@timestamp","buckets":60}}}""")!.AsObject();
        var result = await service.SearchAsync(
            MakeConnection(),
            index: "logs-app-2026",
            query: null,
            sort: null,
            aggs: aggs.Deserialize<JsonElement>(),
            size: 25,
            from: 0,
            sourceIncludes: null,
            CancellationToken.None);

        var expected = JsonNode.Parse(
            """{"my_agg":{"buckets":[{"key_as_string":"2026-11-19T00:00:00.000Z","key":1763462400000,"doc_count":42}],"interval":"1d"}}""");
        Assert.NotNull(result.Aggregations);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(result.Aggregations!.Value.GetRawText()),
            expected));
        Assert.Equal(812, result.Total);
        Assert.Equal(9, result.TookMs);
        Assert.Contains("typed_keys=true", handler.RequestedUris.Single());
    }

    [Fact]
    public async Task Count_ReturnsLong()
    {
        var (service, handler) = CreateService(_ => JsonResponse(HttpStatusCode.OK,
            """{"count":42,"_shards":{"total":1,"successful":1,"skipped":0,"failed":0}}"""));

        var count = await service.CountAsync(MakeConnection(), "logs-app-2026", query: null, CancellationToken.None);

        Assert.Equal(42, count);
        Assert.Equal("POST /logs-app-2026/_count", Assert.Single(handler.RequestedPaths));
        Assert.Contains("match_all", handler.Bodies.Single());
    }

    [Fact]
    public async Task Unauthorized_SurfacesAs401ShapedException()
    {
        var (service, _) = CreateService(_ => JsonResponse(HttpStatusCode.Unauthorized,
            """{"error":{"type":"security_exception","reason":"missing authentication credentials","root_cause":[{"type":"security_exception","reason":"missing authentication credentials"}]},"status":401}"""));

        var exception = await Assert.ThrowsAsync<ElasticsearchUnauthenticatedException>(
            () => service.DocumentsAsync(
                MakeConnection(),
                index: "logs-app-2026",
                from: 0,
                size: 25,
                q: null,
                start: null,
                end: null,
                field: null,
                filters: null,
                logic: null,
                CancellationToken.None));

        Assert.Contains("authentication failed (401)", exception.Message);
    }

    [Fact]
    public async Task WildcardIndex_IsSentVerbatim()
    {
        var (service, handler) = CreateService(_ => JsonResponse(HttpStatusCode.OK,
            """{"took":1,"hits":{"total":{"value":0,"relation":"eq"},"hits":[]}}"""));

        await service.SearchAsync(
            MakeConnection(),
            index: "logs-*",
            query: null,
            sort: null,
            aggs: null,
            size: 1,
            from: 0,
            sourceIncludes: null,
            CancellationToken.None);

        // The transport percent-encodes the path segment; the decoded form must
        // still carry the wildcard exactly as given.
        var requestedUri = Uri.UnescapeDataString(Assert.Single(handler.RequestedUris));
        Assert.Contains("logs-*", requestedUri);
    }

    [Fact]
    public async Task Timeout_SurfacesAsUnavailableException()
    {
        var (service, _) = CreateService(_ => throw new TaskCanceledException("request timed out"));

        var exception = await Assert.ThrowsAsync<ElasticsearchUnavailableException>(
            () => service.DocumentsAsync(
                MakeConnection(),
                index: "logs-app-2026",
                from: 0,
                size: 25,
                q: null,
                start: null,
                end: null,
                field: null,
                filters: null,
                logic: null,
                CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public void Cache_RebuildsOnConnectionStringChange()
    {
        using var cache = new ElasticsearchClientCache(evictionInterval: TimeSpan.FromHours(1));
        var connection = DatabaseConnection.Create(
            "test",
            DatabaseProvider.Elasticsearch,
            "http://cache-a.example:9200/",
            DatabaseEnvironment.Development);

        // Same connection id and same string: identical client.
        var first = cache.GetOrCreate(connection);
        Assert.Same(first, cache.GetOrCreate(connection));

        // Same id, different string: a rebuilt client, not the cached one.
        var changed = connection with { ConnectionString = "http://cache-b.example:9200/" };
        var rebuilt = cache.GetOrCreate(changed);
        Assert.NotSame(first, rebuilt);
        Assert.Same(rebuilt, cache.GetOrCreate(changed));

        // Invalidate drops the entry; the next call rebuilds.
        cache.Invalidate(changed.Id);
        var afterInvalidate = cache.GetOrCreate(changed);
        Assert.NotSame(rebuilt, afterInvalidate);

        // Cap is 8: adding a ninth distinct id evicts the least recently used one.
        var created = new List<ElasticsearchClient>();
        var connections = Enumerable.Range(0, 9)
            .Select(i => DatabaseConnection.Create(
                $"cap-{i}",
                DatabaseProvider.Elasticsearch,
                $"http://cache-cap-{i}.example:9200/",
                DatabaseEnvironment.Development))
            .ToList();
        foreach (var c in connections)
        {
            Thread.Sleep(2);
            created.Add(cache.GetOrCreate(c));
        }

        // The ninth insertion evicted the first of nine: the next read is a new
        // client, while the second entry is still cached.
        Assert.Same(created[1], cache.GetOrCreate(connections[1]));
        Assert.NotSame(created[0], cache.GetOrCreate(connections[0]));
    }

    private static (ElasticsearchReadService Service, StubHttpMessageHandler Handler) CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var stub = new StubHttpMessageHandler(responder);
        var pool = new Elastic.Transport.SingleNodePool(TestCluster);
        var invoker = new Elastic.Transport.HttpRequestInvoker((_, _) => stub);
        var settings = new ElasticsearchClientSettings(pool, invoker)
            .RequestTimeout(TimeSpan.FromSeconds(5))
            .DisableDirectStreaming(true);
        var client = new ElasticsearchClient(settings);
        return (new ElasticsearchReadService(new StubClientCache(client)), stub);
    }

    private static DatabaseConnection MakeConnection() =>
        DatabaseConnection.Create(
            "test",
            DatabaseProvider.Elasticsearch,
            "http://elasticsearch-test:9200/",
            DatabaseEnvironment.Development);

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json)
    {
        var message = new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-Elastic-Product", "Elasticsearch");
        return message;
    }

    private const string CannedSearchResponse = """
    {"took":12,"timed_out":false,"_shards":{"total":1,"successful":1,"skipped":0,"failed":0},"hits":{"total":{"value":124312,"relation":"eq"},"max_score":1.0,"hits":[{"_index":"logs-app-2026","_id":"abc123","_score":1.0,"_source":{"message":"hello","@timestamp":"2026-11-19T00:00:00Z"}}]}}
    """;

    private const string CannedStatsResponse = """
    {"_shards":{"total":4,"successful":4,"skipped":0,"failed":0},
     "indices":{
       "logs-app-2026":{"uuid":"u1","health":"green","status":"open",
         "primaries":{"docs":{"count":124312,"deleted":0},"store":{"size_in_bytes":48211320}},
         "total":{"docs":{"count":248624,"deleted":0},"store":{"size_in_bytes":96422640}},
         "shards":{"0":[{"routing":{"state":"STARTED","primary":true,"node":"n1"},"routing_node":"n1","state":"STARTED","primary":true,"index":"logs-app-2026","shard":0,"docs":{"count":124312},"store":{"size_in_bytes":48211320}},
                        {"routing":{"state":"STARTED","primary":false,"node":"n2"},"routing_node":"n2","state":"STARTED","primary":false,"index":"logs-app-2026","shard":0,"docs":{"count":124312},"store":{"size_in_bytes":48211320}}]}},
       ".kibana":{"uuid":"u2","health":"yellow","status":"open",
         "primaries":{"docs":{"count":10,"deleted":0},"store":{"size_in_bytes":1000}},
         "total":{"docs":{"count":10,"deleted":0},"store":{"size_in_bytes":1000}},
         "shards":{"0":[{"routing":{"state":"STARTED","primary":true,"node":"n1"},"routing_node":"n1","state":"STARTED","primary":true,"index":".kibana","shard":0,"docs":{"count":10},"store":{"size_in_bytes":1000}}]}},
       "logs-sys-2026":{"uuid":"u3","health":"red","status":"open",
         "primaries":{"docs":{"count":555,"deleted":0},"store":{"size_in_bytes":7777}},
         "total":{"docs":{"count":555,"deleted":0},"store":{"size_in_bytes":7777}},
         "shards":{"0":[{"routing":{"state":"STARTED","primary":true,"node":"n1"},"routing_node":"n1","state":"STARTED","primary":true,"index":"logs-sys-2026","shard":0,"docs":{"count":555},"store":{"size_in_bytes":7777}}]}}
     }}
    """;

    private sealed class StubClientCache : IElasticsearchClientCache
    {
        private readonly ElasticsearchClient _client;

        public StubClientCache(ElasticsearchClient client) => _client = client;

        public ElasticsearchClient GetOrCreate(DatabaseConnection connection) => _client;

        public void Invalidate(string connectionId)
        {
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        public List<string> RequestedPaths { get; } = new();

        public List<string> RequestedUris { get; } = new();

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            RequestedPaths.Add(request.Method + " " + request.RequestUri?.AbsolutePath);
            Bodies.Add(await ReadBodyAsync(request, cancellationToken));
            return _responder(request);
        }

        private static async Task<string> ReadBodyAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is null)
            {
                return string.Empty;
            }

            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            if (string.Equals(request.Content.Headers.ContentEncoding?.ToString(), "gzip", StringComparison.OrdinalIgnoreCase))
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                await gzip.CopyToAsync(output, ct);
                bytes = output.ToArray();
            }

            return Encoding.UTF8.GetString(bytes);
        }
    }
}
