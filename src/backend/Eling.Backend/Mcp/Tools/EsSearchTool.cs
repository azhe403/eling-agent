using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class EsSearchTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly IElasticsearchReadService _readService;
    private readonly ILogger<EsSearchTool> _logger;
    private readonly IAuditLogger _audit;

    public EsSearchTool(
        IDatabaseConnectionStore store,
        IElasticsearchReadService readService,
        ILogger<EsSearchTool> logger,
        IAuditLogger audit)
    {
        _store = store;
        _readService = readService;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "es_search"), Description("Run a raw _search on an index on the Elasticsearch cluster (read-only): optional raw query/sort/aggregations, up to 50 rows, from+size capped at 10,000.")]
    public async Task<ElasticsearchSearchResult> SearchAsync(
        [Description("Connection id.")] string connectionId,
        [Description("Index name (exact; may be a wildcard/multi-index).")] string index,
        [Description("Raw `_search` query clause (object, optional — omit for match_all).")] JsonElement? query = null,
        [Description("Raw `_search` sort (optional).")] JsonElement? sort = null,
        [Description("Raw `_search` aggregations (optional).")] JsonElement? aggs = null,
        [Description("Max rows (1-50).")] int size = 25,
        [Description("Offset (clamped so size + offset <= 10000).")] int from = 0,
        [Description("Source fields to return; omit for _source as-is.")] string[]? sourceIncludes = null,
        CancellationToken ct = default)
    {
        var row = await _store.FindAsync(connectionId, ct);
        if (row is null) throw new KeyNotFoundException("connection not found");
        if (row.Provider != DatabaseProvider.Elasticsearch)
        {
            throw new InvalidOperationException(ElasticsearchRequirement.NotElasticsearch(row, "es_search"));
        }

        size = Math.Clamp(size, 1, 50);
        from = Math.Max(0, from);
        if ((long)from + size > 10_000)
        {
            throw new ElasticsearchApiException(400, "from+size exceeds the 10000-window maximum");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _readService.SearchAsync(row, index, query, sort, aggs, size, from, sourceIncludes, ct);
            sw.Stop();
            await _audit.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.EsSearch,
                Outcome = AuditOutcome.Success,
                Target = connectionId,
                Query = index,
                ResultCount = result.Hits.Count,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, ct);
            return result;
        }
        catch (Exception ex) when (ex is ElasticsearchApiException or ElasticsearchUnavailableException or ElasticsearchUnauthenticatedException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "es_search failed for {Id}", connectionId);
            throw TranslateFailure(ex);
        }
        catch (Exception ex) when (ex is not NotSupportedException && ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "es_search failed for {Id}", connectionId);
            throw new InvalidOperationException("es_search failed");
        }
        catch (NotSupportedException)
        {
            sw.Stop();
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
    }

    // Client-safe shape only: the cluster host and account never cross the MCP
    // boundary (mirrors the HTTP endpoint's messages in DatabaseElasticsearchEndpoints).
    private static Exception TranslateFailure(Exception ex) => ex switch
    {
        ElasticsearchApiException api when api.Status is >= 400 and < 500
            => new InvalidOperationException($"elasticsearch rejected the request ({api.Status}): {api.Reason}"),
        ElasticsearchApiException api
            => new InvalidOperationException($"elasticsearch answered with status {api.Status}"),
        ElasticsearchUnauthenticatedException unauthenticated
            => new InvalidOperationException(unauthenticated.CallerSafeDetail),
        _ => new InvalidOperationException("elasticsearch unavailable"),
    };
}
