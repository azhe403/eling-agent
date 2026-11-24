using System.ComponentModel;
using System.Diagnostics;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class EsMappingTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly IElasticsearchReadService _readService;
    private readonly ILogger<EsMappingTool> _logger;
    private readonly IAuditLogger _audit;

    public EsMappingTool(
        IDatabaseConnectionStore store,
        IElasticsearchReadService readService,
        ILogger<EsMappingTool> logger,
        IAuditLogger audit)
    {
        _store = store;
        _readService = readService;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "es_mapping"), Description("Get the raw mappings and settings of an index on the Elasticsearch cluster (read-only).")]
    public async Task<object> MappingAsync(
        [Description("Connection id.")] string connectionId,
        [Description("Index name (exact; may be a wildcard/multi-index — read-only, allowed).")] string index,
        CancellationToken ct = default)
    {
        var row = await _store.FindAsync(connectionId, ct);
        if (row is null) throw new KeyNotFoundException("connection not found");
        if (row.Provider != DatabaseProvider.Elasticsearch)
        {
            throw new InvalidOperationException(ElasticsearchRequirement.NotElasticsearch(row, "es_mapping"));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var detail = await _readService.GetIndexDetailAsync(row, index, ct);
            sw.Stop();
            await _audit.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.EsMapping,
                Outcome = AuditOutcome.Success,
                Target = connectionId,
                Query = index,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, ct);
            // Stats are dropped (spec §4.8): the response is mappings and
            // settings only, as the UI reads them.
            return new { Name = detail.Name, Mappings = detail.Mappings, Settings = detail.Settings };
        }
        catch (Exception ex) when (ex is ElasticsearchApiException or ElasticsearchUnavailableException or ElasticsearchUnauthenticatedException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "es_mapping failed for {Id}", connectionId);
            throw TranslateFailure(ex);
        }
        catch (Exception ex) when (ex is not NotSupportedException && ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "es_mapping failed for {Id}", connectionId);
            throw new InvalidOperationException("es_mapping failed");
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
