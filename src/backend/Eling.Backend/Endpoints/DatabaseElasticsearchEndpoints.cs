using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Endpoints;

/// <summary>
/// The Elasticsearch read routes under /api/databases/{id}/es/. Every route is
/// read-only; PUT/PATCH/DELETE are left unmapped so the router itself answers
/// 405. Responses carry the raw cluster payloads the read service produced
/// (mappings, settings, sources, aggregations) verbatim — nothing is projected
/// or re-merged here. The only envelope rule is the one the spec fixes: the
/// compiled bool travels inside the request's filter array, never merged.
/// </summary>
public static class DatabaseElasticsearchEndpoints
{
    private const int MaxSize = 100;
    private const int MaxBuckets = 100;
    private const int DefaultSize = 25;
    private const int DefaultBuckets = 60;
    private const int MaxResultWindow = 10_000;
    private const int MaxBodyLogBytes = 4096;
    private const string DefaultDateField = "@timestamp";

    private static readonly JsonSerializerOptions FilterJson = new() { PropertyNameCaseInsensitive = true };

    public static WebApplication MapDatabaseElasticsearchEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/databases");

        group.MapGet("/{id}/es/indices", IndicesAsync);
        group.MapGet("/{id}/es/index", IndexAsync);
        group.MapGet("/{id}/es/documents", DocumentsAsync);
        group.MapGet("/{id}/es/histogram", HistogramAsync);
        group.MapPost("/{id}/es/search", SearchAsync);
        group.MapPost("/{id}/es/count", CountAsync);

        return app;
    }

    private static async Task<IResult> IndicesAsync(
        string id,
        HttpContext httpContext,
        IDatabaseConnectionStore store,
        IElasticsearchReadService esService,
        IAuditLogger auditLogger,
        ILoggerFactory loggerFactory,
        bool includeSystem = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var gate = await OpenElasticAsync(id, httpContext, store, "es_indices", ct);
        if (gate.Http is not null) return gate.Http;

        try
        {
            var indices = await esService.ListIndicesAsync(gate.Row!, includeSystem, ct);
            sw.Stop();
            await AuditAsync(auditLogger, DatabaseAuditActions.EsIndices, id, null,
                indices.Count, AuditOutcome.Success, (int)sw.ElapsedMilliseconds, ct);
            return Results.Ok(indices);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await AuditFailureAsync(auditLogger, DatabaseAuditActions.EsIndices, id, null, ex, sw, ct);
            return TranslateElasticFailure(ex, loggerFactory, "es indices");
        }
    }

    private static async Task<IResult> IndexAsync(
        string id,
        HttpContext httpContext,
        IDatabaseConnectionStore store,
        IElasticsearchReadService esService,
        IAuditLogger auditLogger,
        ILoggerFactory loggerFactory,
        string? index,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var gate = await OpenElasticAsync(id, httpContext, store, "es_mapping", ct);
        if (gate.Http is not null) return gate.Http;

        if (string.IsNullOrWhiteSpace(index))
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsMapping,
                id, null, "index is required", sw, ct);
        }

        try
        {
            var detail = await esService.GetIndexDetailAsync(gate.Row!, index, ct);
            sw.Stop();
            await AuditAsync(auditLogger, DatabaseAuditActions.EsMapping, id, index,
                null, AuditOutcome.Success, (int)sw.ElapsedMilliseconds, ct);
            return Results.Ok(detail);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await AuditFailureAsync(auditLogger, DatabaseAuditActions.EsMapping, id, index, ex, sw, ct);
            return TranslateElasticFailure(ex, loggerFactory, "es index");
        }
    }

    private static async Task<IResult> DocumentsAsync(
        string id,
        HttpContext httpContext,
        IDatabaseConnectionStore store,
        IElasticsearchReadService esService,
        IAuditLogger auditLogger,
        ILoggerFactory loggerFactory,
        string? index,
        int from = 0,
        int size = DefaultSize,
        string? q = null,
        string? start = null,
        string? end = null,
        string? field = null,
        string? filters = null,
        string? logic = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var gate = await OpenElasticAsync(id, httpContext, store, "es_documents", ct);
        if (gate.Http is not null) return gate.Http;
        var row = gate.Row!;

        if (string.IsNullOrWhiteSpace(index))
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, null, "index is required", sw, ct);
        }

        var sizeClamped = Math.Clamp(size, 1, MaxSize);
        if (!TryValidateWindow(from, sizeClamped, out var windowError))
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, index, windowError, sw, ct);
        }

        var startTs = ParseTimestamp(start, "start", out var startError);
        if (startError is not null)
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, index, startError, sw, ct);
        }

        var endTs = ParseTimestamp(end, "end", out var endError);
        if (endError is not null)
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, index, endError, sw, ct);
        }

        var filterRows = DecodeFilters(filters, out var filtersError);
        if (filtersError is not null)
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, index, filtersError, sw, ct);
        }

        try
        {
            var page = await esService.DocumentsAsync(
                row, index, from, sizeClamped, q, startTs, endTs, field, filterRows, logic, ct);
            sw.Stop();
            await AuditAsync(auditLogger, DatabaseAuditActions.EsSearch, id, index,
                (int)Math.Min(page.Total, int.MaxValue), AuditOutcome.Success, (int)sw.ElapsedMilliseconds, ct);
            return Results.Ok(page);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await AuditFailureAsync(auditLogger, DatabaseAuditActions.EsSearch, id, index, ex, sw, ct);
            return TranslateElasticFailure(ex, loggerFactory, "es documents");
        }
    }

    private static async Task<IResult> HistogramAsync(
        string id,
        HttpContext httpContext,
        IDatabaseConnectionStore store,
        IElasticsearchReadService esService,
        IAuditLogger auditLogger,
        ILoggerFactory loggerFactory,
        string? index,
        int buckets = DefaultBuckets,
        string? q = null,
        string? start = null,
        string? end = null,
        string? field = null,
        string? filters = null,
        string? logic = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var gate = await OpenElasticAsync(id, httpContext, store, "es_histogram", ct);
        if (gate.Http is not null) return gate.Http;
        var row = gate.Row!;

        if (string.IsNullOrWhiteSpace(index))
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, null, "index is required", sw, ct);
        }

        var bucketsClamped = Math.Clamp(buckets, 1, MaxBuckets);
        var effectiveField = string.IsNullOrWhiteSpace(field) ? DefaultDateField : field.Trim();

        var startTs = ParseTimestamp(start, "start", out var startError);
        if (startError is not null)
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, index, startError, sw, ct);
        }

        var endTs = ParseTimestamp(end, "end", out var endError);
        if (endError is not null)
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, index, endError, sw, ct);
        }

        var filterRows = DecodeFilters(filters, out var filtersError);
        if (filtersError is not null)
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, index, filtersError, sw, ct);
        }

        try
        {
            var rawBuckets = await esService.HistogramAsync(
                row, index, q, startTs, endTs, effectiveField, bucketsClamped, filterRows, logic, ct);
            sw.Stop();
            var ordered = rawBuckets.OrderBy(b => b.KeyMs).ToList();
            var interval = ordered.Select(b => b.Interval).FirstOrDefault(i => i is not null);
            await AuditAsync(auditLogger, DatabaseAuditActions.EsSearch, id, index,
                ordered.Count, AuditOutcome.Success, (int)sw.ElapsedMilliseconds, ct);
            return Results.Ok(new
            {
                field = effectiveField,
                interval,
                buckets = ordered.Select(b => new { b.KeyMs, b.DocCount }).ToList(),
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            await AuditFailureAsync(auditLogger, DatabaseAuditActions.EsSearch, id, index, ex, sw, ct);
            return TranslateElasticFailure(ex, loggerFactory, "es histogram");
        }
    }

    private static async Task<IResult> SearchAsync(
        string id,
        HttpContext httpContext,
        ElasticsearchSearchRequest req,
        IDatabaseConnectionStore store,
        IElasticsearchReadService esService,
        IAuditLogger auditLogger,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var gate = await OpenElasticAsync(id, httpContext, store, "es_search / es_mapping", ct);
        if (gate.Http is not null) return gate.Http;
        var row = gate.Row!;

        if (req is null || string.IsNullOrWhiteSpace(req.Index))
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, null, "index is required", sw, ct);
        }

        var index = req.Index;
        var sizeClamped = Math.Clamp(req.Size, 1, MaxSize);
        if (!TryValidateWindow(req.From, sizeClamped, out var windowError))
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsSearch,
                id, index, windowError, sw, ct);
        }

        var logger = loggerFactory.CreateLogger("DatabaseElasticsearch");
        // The audit entry records the connection and index only; the body goes to
        // the debug log, truncated, and never to the access trail.
        logger.LogDebug("es_search body for {Id}: {Body}", id, Truncate(JsonSerializer.Serialize(req, FilterJson), MaxBodyLogBytes));

        try
        {
            var result = await esService.SearchAsync(
                row, index, req.Query, req.Sort, req.Aggs,
                sizeClamped, req.From, req.SourceIncludes, ct);
            sw.Stop();
            await AuditAsync(auditLogger, DatabaseAuditActions.EsSearch, id, index,
                (int)Math.Min(result.Total, int.MaxValue), AuditOutcome.Success, (int)sw.ElapsedMilliseconds, ct);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await AuditFailureAsync(auditLogger, DatabaseAuditActions.EsSearch, id, index, ex, sw, ct);
            return TranslateElasticFailure(ex, loggerFactory, "es search");
        }
    }

    private static async Task<IResult> CountAsync(
        string id,
        HttpContext httpContext,
        ElasticsearchCountRequest req,
        IDatabaseConnectionStore store,
        IElasticsearchReadService esService,
        IAuditLogger auditLogger,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var gate = await OpenElasticAsync(id, httpContext, store, "es_search / es_mapping", ct);
        if (gate.Http is not null) return gate.Http;
        var row = gate.Row!;

        if (req is null || string.IsNullOrWhiteSpace(req.Index))
        {
            return await DeniedAsync(auditLogger, DatabaseAuditActions.EsCount,
                id, null, "index is required", sw, ct);
        }

        var index = req.Index;
        try
        {
            var count = await esService.CountAsync(row, index, req.Query, ct);
            sw.Stop();
            await AuditAsync(auditLogger, DatabaseAuditActions.EsCount, id, index,
                (int)Math.Min(count, int.MaxValue), AuditOutcome.Success, (int)sw.ElapsedMilliseconds, ct);
            return Results.Ok(new { count, tookMs = (long)sw.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            sw.Stop();
            await AuditFailureAsync(auditLogger, DatabaseAuditActions.EsCount, id, index, ex, sw, ct);
            return TranslateElasticFailure(ex, loggerFactory, "es count");
        }
    }

    /// <summary>
    /// The gate every ES route asks first: the id exists, and the connection is
    /// Elasticsearch. The refusal names the provider or the ES route to use.
    /// </summary>
    private static async Task<ElasticGate> OpenElasticAsync(
        string id,
        HttpContext httpContext,
        IDatabaseConnectionStore store,
        string operation,
        CancellationToken ct)
    {
        var row = await store.FindAsync(id, ct);
        if (row is null)
        {
            return ElasticGate.NotFound(httpContext, id);
        }

        if (row.Provider != DatabaseProvider.Elasticsearch)
        {
            return ElasticGate.BadRequest(ElasticsearchRequirement.NotElasticsearch(row, operation));
        }

        return ElasticGate.Ok(row);
    }

    private static bool TryValidateWindow(int from, int size, out string error)
    {
        error = string.Empty;
        if (from < 0)
        {
            error = "from must be >= 0";
            return false;
        }

        if ((long)from + size > MaxResultWindow)
        {
            error = $"from + size must be <= 10,000 (got {from} + {size})";
            return false;
        }

        return true;
    }

    private static DateTimeOffset? ParseTimestamp(string? value, string name, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
        {
            return ts;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        error = $"{name} must be an ISO-8601 timestamp or epoch milliseconds";
        return null;
    }

    private static List<ElasticsearchFilterRow>? DecodeFilters(string? filters, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(filters))
        {
            return null;
        }

        try
        {
            var rows = JsonSerializer.Deserialize<List<ElasticsearchFilterRow>>(filters, FilterJson);
            if (rows is null)
            {
                error = "filters must be a JSON array of filter rows";
                return null;
            }

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Field))
                {
                    error = "each filter row needs a field";
                    return null;
                }

                if (string.IsNullOrWhiteSpace(row.Op))
                {
                    error = "each filter row needs an op";
                    return null;
                }
            }

            return rows;
        }
        catch (JsonException)
        {
            error = "filters must be a JSON array of filter rows";
            return null;
        }
    }

    private static string Truncate(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        return Encoding.UTF8.GetString(bytes, 0, maxBytes) + "…[truncated]";
    }

    private static string FailureTag(Exception ex) => ex switch
    {
        ElasticsearchApiException api => api.Status.ToString(CultureInfo.InvariantCulture),
        ElasticsearchUnauthenticatedException => "401",
        ElasticsearchUnavailableException => "unavailable",
        ElasticsearchFilterException => "filter",
        _ => "error",
    };

    private static async Task AuditAsync(
        IAuditLogger auditLogger,
        string action,
        string id,
        string? query,
        int? resultCount,
        AuditOutcome outcome,
        int durationMs,
        CancellationToken ct,
        string? verb = null) =>
        await auditLogger.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Access,
            Category = AuditCategory.Database,
            Action = action,
            Actor = "dashboard",
            Target = id,
            Outcome = outcome,
            Query = query,
            ResultCount = resultCount,
            DurationMs = durationMs,
            Verb = verb,
        }, ct);

    private static Task AuditFailureAsync(
        IAuditLogger auditLogger,
        string action,
        string id,
        string? query,
        Exception ex,
        Stopwatch sw,
        CancellationToken ct) =>
        AuditAsync(auditLogger, action, id, query, null, AuditOutcome.Error,
            (int)sw.ElapsedMilliseconds, ct, FailureTag(ex));

    private static async Task<IResult> DeniedAsync(
        IAuditLogger auditLogger,
        string action,
        string id,
        string? query,
        string reason,
        Stopwatch sw,
        CancellationToken ct)
    {
        sw.Stop();
        await AuditAsync(auditLogger, action, id, query, null, AuditOutcome.Denied,
            (int)sw.ElapsedMilliseconds, ct);
        return Results.BadRequest(reason);
    }

    private static IResult TranslateElasticFailure(Exception ex, ILoggerFactory loggerFactory, string operation) => ex switch
    {
        ElasticsearchFilterException filter => CreateFilterResult(filter, loggerFactory, operation),
        ElasticsearchApiException api => CreateApiResult(api, loggerFactory, operation),
        ElasticsearchUnauthenticatedException unauthenticated => CreateUnauthenticatedResult(unauthenticated, loggerFactory, operation),
        ElasticsearchUnavailableException unavailable => CreateUnavailableResult(unavailable, loggerFactory, operation),
        _ => CreateUnexpectedResult(ex, loggerFactory, operation),
    };

    // The compiler's messages are caller-safe by construction: they name the
    // offending field op or the constraint that was violated, never the host.
    private static IResult CreateFilterResult(ElasticsearchFilterException filter, ILoggerFactory loggerFactory, string operation)
    {
        loggerFactory.CreateLogger("DatabaseElasticsearch")
            .LogDebug(filter, "Elasticsearch filter rejected the request for {Operation}", operation);
        return Results.BadRequest(filter.Message);
    }

    private static IResult CreateApiResult(ElasticsearchApiException api, ILoggerFactory loggerFactory, string operation)
    {
        var logger = loggerFactory.CreateLogger("DatabaseElasticsearch");
        if (api.Status is 401 or 403)
        {
            // Never echo the cluster's account-naming reason here; the fix is the same either way.
            logger.LogWarning(api, "Elasticsearch authentication failed for {Operation}", operation);
            return Results.Problem(statusCode: 502, title: "authentication failed",
                detail: $"authentication failed ({api.Status}): the cluster requires credentials");
        }

        if (api.Status == 404)
        {
            logger.LogDebug(api, "Elasticsearch resource not found for {Operation}", operation);
            return Results.Problem(statusCode: 404, title: "index not found",
                detail: string.IsNullOrWhiteSpace(api.Reason)
                    ? "the index is not present in the cluster"
                    : api.Reason);
        }

        if (api.Status is >= 400 and < 500)
        {
            // A 4xx reason is the caller's own bad request (e.g. a malformed
            // query_string) and is safe to show verbatim.
            logger.LogDebug(api, "Elasticsearch rejected the request for {Operation}", operation);
            return Results.BadRequest(string.IsNullOrWhiteSpace(api.Reason)
                ? "the cluster rejected the request"
                : api.Reason);
        }

        logger.LogWarning(api, "Elasticsearch answered {Status} for {Operation}", api.Status, operation);
        return Results.Problem(statusCode: 502, title: "elasticsearch error",
            detail: $"the cluster answered with status {api.Status}");
    }

    private static IResult CreateUnauthenticatedResult(ElasticsearchUnauthenticatedException unauthenticated, ILoggerFactory loggerFactory, string operation)
    {
        // The exception text can name the account; the fix is the same without it.
        loggerFactory.CreateLogger("DatabaseElasticsearch")
            .LogWarning(unauthenticated, "Elasticsearch authentication failed for {Operation}", operation);
        return Results.Problem(statusCode: 502, title: "authentication failed",
            detail: "authentication failed (401): the cluster requires credentials");
    }

    private static IResult CreateUnavailableResult(ElasticsearchUnavailableException unavailable, ILoggerFactory loggerFactory, string operation)
    {
        // The exception text can name the host; the caller only needs to know to retry.
        loggerFactory.CreateLogger("DatabaseElasticsearch")
            .LogWarning(unavailable, "Elasticsearch is unavailable for {Operation}", operation);
        return Results.Problem(statusCode: 503, title: "elasticsearch unavailable",
            detail: "elasticsearch unavailable");
    }

    private static IResult CreateUnexpectedResult(Exception ex, ILoggerFactory loggerFactory, string operation)
    {
        // Never the exception text: it can carry the host and the account.
        loggerFactory.CreateLogger("DatabaseElasticsearch")
            .LogError(ex, "Unexpected Elasticsearch failure for {Operation}", operation);
        return Results.Problem(statusCode: 500, title: "elasticsearch failed",
            detail: "an unexpected error occurred");
    }

    /// <summary>
    /// One of the two pre-conditions, or the connection row when both held.
    /// Mirrors the Redis key gate's shape.
    /// </summary>
    private sealed class ElasticGate
    {
        public IResult? Http { get; private init; }
        public DatabaseConnection? Row { get; private init; }
        public static ElasticGate Ok(DatabaseConnection row) => new() { Row = row };
        public static ElasticGate NotFound(HttpContext httpContext, string id) =>
            new() { Http = ConnectionNotFoundResult.Create(id, httpContext) };
        public static ElasticGate BadRequest(string detail) =>
            new() { Http = Results.BadRequest(detail) };
    }
}
