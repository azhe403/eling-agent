using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class EsIndicesTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly IElasticsearchReadService _readService;
    private readonly ILogger<EsIndicesTool> _logger;
    private readonly IAuditLogger _audit;

    public EsIndicesTool(
        IDatabaseConnectionStore store,
        IElasticsearchReadService readService,
        ILogger<EsIndicesTool> logger,
        IAuditLogger audit)
    {
        _store = store;
        _readService = readService;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "es_indices"), Description("List the indices on the Elasticsearch cluster (read-only): names, health, status, doc counts and shard stats.")]
    public async Task<ElasticsearchIndexInfo[]> IndicesAsync(
        [Description("Connection id.")] string connectionId,
        [Description("Include system indices (names starting with '.') (default false).")] bool includeSystem = false,
        CancellationToken ct = default)
    {
        var row = await _store.FindAsync(connectionId, ct);
        if (row is null) throw new KeyNotFoundException("connection not found");
        if (row.Provider != DatabaseProvider.Elasticsearch)
        {
            throw new InvalidOperationException(ElasticsearchRequirement.NotElasticsearch(row, "es_indices"));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var indices = await _readService.ListIndicesAsync(row, includeSystem, ct);
            sw.Stop();
            await _audit.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.EsIndices,
                Outcome = AuditOutcome.Success,
                Target = connectionId,
                ResultCount = indices.Count,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, ct);
            return indices.ToArray();
        }
        catch (Exception ex) when (ex is ElasticsearchApiException or ElasticsearchUnavailableException or ElasticsearchUnauthenticatedException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "es_indices failed for {Id}", connectionId);
            throw TranslateFailure(ex);
        }
        catch (Exception ex) when (ex is not NotSupportedException && ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "es_indices failed for {Id}", connectionId);
            throw new InvalidOperationException("es_indices failed");
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
