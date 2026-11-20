using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Aggregations;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Database;

/// <summary>
/// The read-only Elasticsearch read service. Read-only is enforced by
/// construction: no method on this class creates, updates, deletes or reindexes
/// anything, and <see cref="IElasticsearchReadService"/> cannot grow a write-shaped
/// member without the convention test failing.
/// </summary>
/// <remarks>
/// Implementation notes against the 8.19.27 client:
/// <list type="bullet">
/// <item>Requests are authored as raw JSON envelopes and deserialized into the
/// client's typed request types via <c>client.RequestResponseSerializer</c>, which is
/// the client's own source-generated serializer. This keeps raw <c>query</c>/<c>sort</c>/<c>aggs</c>
/// passthrough possible while the transport still owns the HTTP call.</item>
/// <item>The <c>_search</c> typed response parser only understands <c>typed_keys=true</c>
/// aggregation blocks, so requests that carry <c>aggs</c> set
/// <c>SearchRequest.TypedKeys</c>; the wire then prefixes aggregation names with
/// <c>variant#</c> and the service strips those prefixes again, so callers see the
/// aggregations exactly as they sent them.</item>
/// <item>Responses are buffered (<c>DisableDirectStreaming</c>) so the raw cluster
/// payload is available for verbatim passthrough; error responses carry the cluster's
/// own reason via <c>ElasticsearchServerError</c>.</item>
/// </list>
/// </remarks>
public sealed class ElasticsearchReadService : IElasticsearchReadService
{
    private const string DefaultDateField = "@timestamp";
    private const int MaxSize = 100;
    private const int MaxBuckets = 100;
    private static readonly Regex Es7VersionPattern = new(
        @"^7\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FieldPattern = new(
        "^[A-Za-z0-9_.@-]{1,255}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IElasticsearchClientCache _cache;
    private readonly ILogger<ElasticsearchReadService>? _logger;

    public ElasticsearchReadService(
        IElasticsearchClientCache cache,
        ILogger<ElasticsearchReadService>? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;
    }

    public async Task<ElasticsearchPingResult> PingAsync(DatabaseConnection connection, CancellationToken ct)
    {
        var client = _cache.GetOrCreate(connection);
        var watch = Stopwatch.StartNew();
        InfoResponse response;
        try
        {
            response = await client.InfoAsync(ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            throw MapTransportFailure("ping", ex);
        }

        watch.Stop();
        EnsureValid("ping", response.IsValidResponse, response.ApiCallDetails, response.ElasticsearchServerError);

        var version = response.Version?.Number;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ElasticsearchUnavailableException("the cluster did not return a version number");
        }

        if (Es7VersionPattern.IsMatch(version))
        {
            throw new NotSupportedException(ElasticsearchRequirement.UnsupportedVersion);
        }

        return new ElasticsearchPingResult(version, watch.ElapsedMilliseconds);
    }

    public async Task<IReadOnlyList<ElasticsearchIndexInfo>> ListIndicesAsync(
        DatabaseConnection connection,
        bool includeSystem,
        CancellationToken ct)
    {
        var client = _cache.GetOrCreate(connection);
        var response = await SendAsync("indices stats", ct, () => client.Indices.StatsAsync(ct));
        EnsureValid("indices stats", response.IsValidResponse, response.ApiCallDetails, response.ElasticsearchServerError);

        var indices = new List<ElasticsearchIndexInfo>();
        if (response.Indices is { } perIndex)
        {
            foreach (var pair in perIndex)
            {
                var name = pair.Key;
                var stats = pair.Value;

                if (!includeSystem && name.StartsWith('.'))
                {
                    // includeSystem=false hides dot-prefixed system indices (spec §4.2).
                    continue;
                }

                var docs = stats.Primaries?.Docs?.Count ?? 0;
                var storeBytes = stats.Primaries?.Store?.SizeInBytes ?? 0;
                var primaryShards = stats.Shards?.Count ?? 0;
                var replicaCopies = stats.Shards is null
                    ? 0
                    : stats.Shards.Values.SelectMany(copies => copies).Count(shard => shard.Routing?.Primary == false);
                var replicas = primaryShards == 0 ? 0 : replicaCopies / primaryShards;
                var health = stats.Health?.ToString()?.ToLowerInvariant() ?? "unknown";
                var status = stats.Status?.ToString()?.ToLowerInvariant() ?? "unknown";
                indices.Add(new ElasticsearchIndexInfo(name, health, status, docs, storeBytes, primaryShards, replicas));
            }
        }

        return indices.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<ElasticsearchIndexDetail> GetIndexDetailAsync(
        DatabaseConnection connection,
        string index,
        CancellationToken ct)
    {
        EnsureIndexName(index);

        var client = _cache.GetOrCreate(connection);

        var mappingResponse = await SendAsync("get mapping", ct, () => client.Indices.GetMappingAsync((Indices)index, ct));
        EnsureValid("get mapping", mappingResponse.IsValidResponse, mappingResponse.ApiCallDetails, mappingResponse.ElasticsearchServerError);

        var settingsResponse = await SendAsync("get settings", ct, () => client.Indices.GetSettingsAsync((Indices)index, ct));
        EnsureValid("get settings", settingsResponse.IsValidResponse, settingsResponse.ApiCallDetails, settingsResponse.ElasticsearchServerError);

        var statsResponse = await SendAsync("index stats", ct, () => client.Indices.StatsAsync((Indices)index, ct));
        EnsureValid("index stats", statsResponse.IsValidResponse, statsResponse.ApiCallDetails, statsResponse.ElasticsearchServerError);

        var mappings = ExtractRawObject(mappingResponse.ApiCallDetails?.ResponseBodyInBytes, index, "mappings");
        var settings = ExtractRawObject(settingsResponse.ApiCallDetails?.ResponseBodyInBytes, index, "settings");

        long docs = 0;
        long storeBytes = 0;
        if (statsResponse.Indices is { } stats)
        {
            // The stats response is keyed by index name: prefer the exact entry
            // (concrete index), otherwise sum every matched index (wildcard).
            if (stats.TryGetValue(index, out var exact))
            {
                docs = exact.Primaries?.Docs?.Count ?? 0;
                storeBytes = exact.Primaries?.Store?.SizeInBytes ?? 0;
            }
            else
            {
                foreach (var pair in stats)
                {
                    var perIndex = pair.Value;
                    docs += perIndex.Primaries?.Docs?.Count ?? 0;
                    storeBytes += perIndex.Primaries?.Store?.SizeInBytes ?? 0;
                }
            }
        }

        return new ElasticsearchIndexDetail(index, mappings, settings, docs, storeBytes);
    }

    public async Task<ElasticsearchDocumentPage> DocumentsAsync(
        DatabaseConnection connection,
        string index,
        int from,
        int size,
        string? q,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? field,
        IReadOnlyList<ElasticsearchFilterRow>? filters,
        string? logic,
        CancellationToken ct)
    {
        EnsureIndexName(index);

        var compiled = CompileFilters(filters, logic);
        var client = _cache.GetOrCreate(connection);

        // The on-demand rule (spec §4.4): the mapping is only consulted when the
        // caller supplied a time range or an explicit field. Browsing a dateless
        // index without a range keeps working and costs no validation call.
        var dateField = string.IsNullOrWhiteSpace(field) ? DefaultDateField : field;
        if (start is not null || end is not null || !string.IsNullOrWhiteSpace(field))
        {
            EnsureValidFieldName(dateField);
            await EnsureDateFieldAsync(client, index, dateField, ct);
        }

        var sizeClamped = Math.Clamp(size, 1, MaxSize);
        var fromClamped = Math.Max(0, from);
        var body = new JsonObject
        {
            ["size"] = sizeClamped,
            ["from"] = fromClamped,
            ["sort"] = new JsonArray(new JsonObject { ["_doc"] = new JsonObject { ["order"] = "asc" } }),
            ["track_total_hits"] = true,
            ["query"] = BuildQueryNode(q, start, end, dateField, compiled),
        };

        var response = await SendSearchAsync(client, index, body, typedKeys: false, ct, "documents");

        return new ElasticsearchDocumentPage(
            response.Total,
            fromClamped,
            sizeClamped,
            response.Took,
            ProjectHits(response.Hits));
    }

    public async Task<IReadOnlyList<ElasticsearchHistogramBucket>> HistogramAsync(
        DatabaseConnection connection,
        string index,
        string? q,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? field,
        int buckets,
        IReadOnlyList<ElasticsearchFilterRow>? filters,
        string? logic,
        CancellationToken ct)
    {
        EnsureIndexName(index);

        var client = _cache.GetOrCreate(connection);

        // Validation order is deliberate: malformed filter rows fail before any
        // HTTP call (the handler must not see them), and a non-date field fails
        // before the search call, after the one unavoidable mapping read.
        var compiled = CompileFilters(filters, logic);
        var dateField = string.IsNullOrWhiteSpace(field) ? DefaultDateField : field;
        EnsureValidFieldName(dateField);
        await EnsureDateFieldAsync(client, index, dateField, ct);

        var bucketClamped = Math.Clamp(buckets, 1, MaxBuckets);
        var body = new JsonObject
        {
            ["size"] = 0,
            ["from"] = 0,
            ["track_total_hits"] = true,
            ["query"] = BuildQueryNode(q, start, end, dateField, compiled),
            ["aggs"] = new JsonObject
            {
                ["histogram"] = new JsonObject
                {
                    ["auto_date_histogram"] = new JsonObject
                    {
                        ["field"] = dateField,
                        ["buckets"] = bucketClamped,
                    },
                },
            },
        };

        var response = await SendSearchAsync(client, index, body, typedKeys: true, ct, "histogram");

        var bucketsResult = new List<ElasticsearchHistogramBucket>();
        if (response.Aggregations is { } dictionary
            && dictionary.TryGetValue("histogram", out var aggregate)
            && aggregate is AutoDateHistogramAggregate histogram)
        {
            foreach (var bucket in histogram.Buckets)
            {
                bucketsResult.Add(new ElasticsearchHistogramBucket(
                    bucket.Key.ToUnixTimeMilliseconds(),
                    bucket.DocCount)
                {
                    Interval = histogram.Interval,
                });
            }
        }

        return bucketsResult;
    }

    public async Task<ElasticsearchSearchResult> SearchAsync(
        DatabaseConnection connection,
        string index,
        JsonElement? query,
        JsonElement? sort,
        JsonElement? aggs,
        int size,
        int from,
        IReadOnlyList<string>? sourceIncludes,
        CancellationToken ct)
    {
        EnsureIndexName(index);

        var client = _cache.GetOrCreate(connection);
        var sizeClamped = Math.Clamp(size, 1, MaxSize);
        var fromClamped = Math.Max(0, from);

        var body = new JsonObject
        {
            ["size"] = sizeClamped,
            ["from"] = fromClamped,
            ["track_total_hits"] = true,
        };
        body["query"] = query is { } queryElement
            ? JsonNode.Parse(queryElement.GetRawText())
            : new JsonObject { ["match_all"] = new JsonObject() };
        if (sort is { } sortElement)
        {
            body["sort"] = JsonNode.Parse(sortElement.GetRawText());
        }

        if (aggs is { } aggsElement)
        {
            body["aggs"] = JsonNode.Parse(aggsElement.GetRawText());
        }

        if (sourceIncludes is { Count: > 0 })
        {
            body["_source"] = new JsonObject
            {
                ["includes"] = new JsonArray(sourceIncludes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            };
        }

        var response = await SendSearchAsync(client, index, body, typedKeys: aggs is not null, ct, "search");

        JsonElement? aggregations = null;
        if (aggs is not null)
        {
            aggregations = ExtractAggregations(response);
        }

        return new ElasticsearchSearchResult(
            response.Total,
            response.Took,
            ProjectHits(response.Hits),
            aggregations);
    }

    public async Task<long> CountAsync(
        DatabaseConnection connection,
        string index,
        JsonElement? query,
        CancellationToken ct)
    {
        EnsureIndexName(index);

        var client = _cache.GetOrCreate(connection);
        var body = new JsonObject
        {
            ["query"] = query is { } countQueryElement
                ? JsonNode.Parse(countQueryElement.GetRawText())
                : new JsonObject { ["match_all"] = new JsonObject() },
        };

        var request = DeserializeBody<CountRequest>(client, body, "count");
        request.Indices = index;
        var response = await SendAsync("count", ct, () => client.CountAsync(request, ct));
        EnsureValid("count", response.IsValidResponse, response.ApiCallDetails, response.ElasticsearchServerError);
        return response.Count;
    }

    private static JsonElement ExtractAggregations(SearchResponse<JsonElement> response)
    {
        // Verbatim passthrough: read the raw cluster payload, strip the
        // typed_keys variant# prefixes the client forced us to request, and hand
        // the aggregations to the caller exactly as it sent them.
        var bytes = response.ApiCallDetails?.ResponseBodyInBytes;
        if (bytes is null || bytes.Length == 0)
        {
            throw new ElasticsearchUnavailableException("the cluster did not return a response body");
        }

        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("aggregations", out var aggregations))
        {
            throw new ElasticsearchApiException(500, "the cluster returned no aggregations block");
        }

        var node = JsonNode.Parse(aggregations.GetRawText());
        StripTypedKeys(node);
        return (node ?? new JsonObject()).Deserialize<JsonElement>();
    }

    private static void StripTypedKeys(JsonNode? node)
    {
        // AggregateDictionary on the wire prefixes every aggregation name with
        // "<variant>#" when typed_keys=true (which the typed response parser
        // requires). Sub-aggregations nest the same way; keys without '#' are
        // untouched bucket keys. Known limitation: arbitrary user content inside
        // an aggregation's meta object carrying '#' in a key is also rewritten -
        // accepted for this passthrough and documented in the report.
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                var value = obj[key];
                var hash = key.IndexOf('#');
                if (hash >= 0)
                {
                    obj.Remove(key);
                    obj.Add(key[(hash + 1)..], value);
                }

                StripTypedKeys(value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                StripTypedKeys(child);
            }
        }
    }

    private async Task<SearchResponse<JsonElement>> SendSearchAsync(
        ElasticsearchClient client,
        string index,
        JsonObject body,
        bool typedKeys,
        CancellationToken ct,
        string operation)
    {
        var request = DeserializeBody<SearchRequest>(client, body, operation);
        request.Indices = index;
        if (typedKeys)
        {
            request.TypedKeys = true;
        }

        var response = await SendAsync(operation, ct, () => client.SearchAsync<JsonElement>(request, ct));
        EnsureValid(operation, response.IsValidResponse, response.ApiCallDetails, response.ElasticsearchServerError);
        return response;
    }

    private static TRequest DeserializeBody<TRequest>(
        ElasticsearchClient client,
        JsonObject body,
        string operation)
        where TRequest : class
    {
        var json = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        try
        {
            // The client's own request/response serializer: the same source-generated
            // converters the transport uses to read the cluster would use to read our
            // JSON envelope, so a round trip here preserves the request faithfully.
            return client.RequestResponseSerializer.Deserialize<TRequest>(stream)
                ?? throw new ElasticsearchUnavailableException($"{operation}: could not build the request");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new ElasticsearchApiException(400, $"{operation}: the request is not valid: {ex.Message}");
        }
    }

    private static IReadOnlyList<ElasticsearchHit> ProjectHits(
        IReadOnlyCollection<Elastic.Clients.Elasticsearch.Core.Search.Hit<JsonElement>>? hits) =>
        hits is null
            ? Array.Empty<ElasticsearchHit>()
            : hits.Select(h => new ElasticsearchHit(
                h.Index ?? string.Empty,
                h.Id ?? string.Empty,
                h.Score,
                SourceOf(h.Source))).ToList();

    private static JsonElement SourceOf(JsonElement source)
    {
        if (source.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }

        return source;
    }

    private static JsonObject BuildQueryNode(
        string? q,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string field,
        JsonElement? compiled)
    {
        var filters = new JsonArray();

        if (!string.IsNullOrWhiteSpace(q))
        {
            filters.Add(new JsonObject
            {
                ["query_string"] = new JsonObject { ["query"] = q },
            });
        }

        if (compiled is { } compiledElement)
        {
            filters.Add(JsonNode.Parse(compiledElement.GetRawText()));
        }

        if (start is not null || end is not null)
        {
            var bounds = new JsonObject();
            if (start is not null)
            {
                bounds["gte"] = start.Value.ToString("o", CultureInfo.InvariantCulture);
            }

            if (end is not null)
            {
                bounds["lte"] = end.Value.ToString("o", CultureInfo.InvariantCulture);
            }

            filters.Add(new JsonObject
            {
                ["range"] = new JsonObject { [field] = bounds },
            });
        }

        if (filters.Count == 0)
        {
            return new JsonObject { ["match_all"] = new JsonObject() };
        }

        return new JsonObject
        {
            ["bool"] = new JsonObject
            {
                ["must"] = new JsonArray { new JsonObject { ["match_all"] = new JsonObject() } },
                ["filter"] = filters,
            },
        };
    }

    private static JsonElement? CompileFilters(IReadOnlyList<ElasticsearchFilterRow>? rows, string? logic)
    {
        if (rows is null || rows.Count == 0)
        {
            return null;
        }

        try
        {
            return ElasticsearchFilterCompiler.Compile(rows, logic);
        }
        catch (ElasticsearchFilterException ex)
        {
            // The compiler's message is safe to show as the 400 reason (spec §4.1).
            throw new ElasticsearchApiException(400, ex.Message);
        }
    }

    private static void EnsureIndexName(string index)
    {
        if (string.IsNullOrWhiteSpace(index))
        {
            // ES treats an absent index as "every index on the cluster"; for an
            // agent-facing read tool that is a footgun, not a feature.
            throw new ElasticsearchApiException(400, "index must not be blank");
        }
    }

    private static void EnsureValidFieldName(string field)
    {
        if (!FieldPattern.IsMatch(field))
        {
            throw new ElasticsearchApiException(400, $"invalid field '{field}': must match ^[A-Za-z0-9_.@-]{{1,255}}$");
        }
    }

    private async Task EnsureDateFieldAsync(
        ElasticsearchClient client,
        string index,
        string field,
        CancellationToken ct)
    {
        var response = await SendAsync("get mapping", ct, () => client.Indices.GetMappingAsync((Indices)index, ct));
        EnsureValid("get mapping", response.IsValidResponse, response.ApiCallDetails, response.ElasticsearchServerError);

        var bytes = response.ApiCallDetails?.ResponseBodyInBytes;
        if (bytes is null || bytes.Length == 0)
        {
            throw new ElasticsearchUnavailableException("the cluster did not return the mapping body");
        }

        using var doc = JsonDocument.Parse(bytes);
        string? nonDateType = null;
        foreach (var indexEntry in doc.RootElement.EnumerateObject())
        {
            if (!indexEntry.Value.TryGetProperty("mappings", out var mappings))
            {
                continue;
            }

            var type = TryGetFieldType(mappings, field);
            if (type is null)
            {
                continue;
            }

            if (string.Equals(type, "date", StringComparison.OrdinalIgnoreCase))
            {
                // A wildcard matched at least one date-mapped index: let the cluster
                // validate the others shard by shard (it returns 400 for those).
                return;
            }

            nonDateType ??= type;
        }

        if (nonDateType is not null)
        {
            throw new ElasticsearchApiException(400, $"field '{field}' in index '{index}' must map to a date; mapped as '{nonDateType}'");
        }

        throw new ElasticsearchApiException(400, $"field '{field}' in index '{index}' is not mapped; it must map to a date");
    }

    private static string? TryGetFieldType(JsonElement mappings, string field)
    {
        JsonElement current = mappings;
        foreach (var segment in field.Split('.'))
        {
            if (!current.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!properties.TryGetProperty(segment, out var node) || node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                return type.GetString();
            }

            // A nested object without a type of its own: descend into it.
            current = node;
        }

        return null;
    }

    private static JsonElement ExtractRawObject(byte[]? body, string index, string property)
    {
        if (body is null || body.Length == 0)
        {
            throw new ElasticsearchUnavailableException("the cluster did not return a response body");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty(index, out var indexEntry))
        {
            throw new ElasticsearchApiException(404, $"index '{index}' is not present in the cluster response");
        }

        if (!indexEntry.TryGetProperty(property, out var value))
        {
            throw new ElasticsearchApiException(500, $"the cluster response for index '{index}' carries no '{property}' block");
        }

        return value.Clone();
    }

    private async Task<TResponse> SendAsync<TResponse>(
        string operation,
        CancellationToken ct,
        Func<Task<TResponse>> call)
        where TResponse : class
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            throw MapTransportFailure(operation, ex);
        }
    }

    private static bool IsCallerCancellation(Exception ex, CancellationToken ct) =>
        ct.IsCancellationRequested && ex is OperationCanceledException;

    private static Exception MapTransportFailure(string operation, Exception ex) => ex switch
    {
        Elastic.Transport.UnexpectedTransportException or Elastic.Transport.TransportException
            => new ElasticsearchUnavailableException(ex.InnerException?.Message ?? ex.Message),
        Elastic.Clients.Elasticsearch.UnsupportedProductException
            => new ElasticsearchUnavailableException(ex.Message),
        JsonException
            => new ElasticsearchApiException(400, $"{operation}: the request is not valid: {ex.Message}"),
        _
            => new ElasticsearchUnavailableException(ex.Message),
    };

    private static ClientFailure FailureOf(
        Elastic.Transport.ApiCallDetails? details,
        Elastic.Transport.Products.Elasticsearch.ElasticsearchServerError? serverError) =>
        new(details?.HttpStatusCode, serverError?.Error?.Reason, details?.OriginalException);

    private static void EnsureValid(
        string operation,
        bool isValid,
        Elastic.Transport.ApiCallDetails? details,
        Elastic.Transport.Products.Elasticsearch.ElasticsearchServerError? serverError)
    {
        if (isValid)
        {
            return;
        }

        ThrowIfInvalid(operation, FailureOf(details, serverError));
    }

    private static void ThrowIfInvalid(string operation, ClientFailure failure)
    {
        if (failure.Status is 401 or 403)
        {
            throw new ElasticsearchUnauthenticatedException(
                $"authentication failed ({failure.Status}): {failure.ServerReason ?? "the cluster requires credentials"}");
        }

        if (failure.Status is int status && status > 0)
        {
            throw new ElasticsearchApiException(status, failure.ServerReason ?? $"unexpected response from the cluster ({status})");
        }

        throw new ElasticsearchUnavailableException(
            failure.OriginalException?.Message ?? failure.ServerReason ?? $"{operation}: the cluster did not respond");
    }

    private readonly record struct ClientFailure(
        int? Status,
        string? ServerReason,
        Exception? OriginalException);
}
