using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class EsCountTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly IElasticsearchReadService _readService;
    private readonly ILogger<EsCountTool> _logger;
    private readonly IAuditLogger _audit;

    public EsCountTool(
        IDatabaseConnectionStore store,
        IElasticsearchReadService readService,
        ILogger<EsCountTool> logger,
        IAuditLogger audit)
    {
        _store = store;
        _readService = readService;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "es_count"), Description("Count documents matching a raw _search query clause on an index on the Elasticsearch cluster (read-only).")]
    public async Task<object> CountAsync(
        [Description("Connection id.")] string connectionId,
        [Description("Index name (exact; may be a wildcard/multi-index).")] string index,
        [Description("Raw `_search` query clause (optional — omit for match_all).")] JsonElement? query = null,
        CancellationToken ct = default)
    {
        var row = await _store.FindAsync(connectionId, ct);
        if (row is null) throw new KeyNotFoundException("connection not found");
        if (row.Provider != DatabaseProvider.Elasticsearch)
        {
            throw new InvalidOperationException(ElasticsearchRequirement.NotElasticsearch(row, "es_count"));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var count = await _readService.CountAsync(row, index, query, ct);
            sw.Stop();
            await _audit.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.EsCount,
                Outcome = AuditOutcome.Success,
                Target = connectionId,
                Query = index,
                ResultCount = (int)Math.Min(count, int.MaxValue),
                DurationMs = (int)sw.ElapsedMilliseconds
            }, ct);
            return new { Count = count, TookMs = (long)sw.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is ElasticsearchApiException or ElasticsearchUnavailableException or ElasticsearchUnauthenticatedException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "es_count failed for {Id}", connectionId);
            throw TranslateFailure(ex);
        }
        catch (Exception ex) when (ex is not NotSupportedException && ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "es_count failed for {Id}", connectionId);
            throw new InvalidOperationException("es_count failed");
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
        ElasticsearchUnauthenticatedException
            => new InvalidOperationException("authentication failed (401): the cluster requires credentials"),
        _ => new InvalidOperationException("elasticsearch unavailable"),
    };
}
