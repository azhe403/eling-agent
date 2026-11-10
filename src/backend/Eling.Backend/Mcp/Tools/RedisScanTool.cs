using System.ComponentModel;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class RedisScanTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly IRedisKeyReadService _readService;
    private readonly ILogger<RedisScanTool> _logger;
    private readonly IAuditLogger _audit;

    public RedisScanTool(
        IDatabaseConnectionStore store,
        IRedisKeyReadService readService,
        ILogger<RedisScanTool> logger,
        IAuditLogger audit)
    {
        _store = store;
        _readService = readService;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "redis_scan"), Description("Browse a Redis connection as a lazy key tree. Returns one page of nodes (prefixes) and leaves (keys) under the given prefix, plus a cursor to continue. Never scans the whole keyspace in one call (pageSize capped at 500).")]
    public async Task<KeyPage> ScanAsync(
        [Description("Connection id.")] string connectionId,
        [Description("Logical database index (0-15).")] int database = 0,
        [Description("Prefix to browse under; empty for root.")] string? prefix = null,
        [Description("Resume cursor from a previous page.")] long cursor = 0,
        [Description("Resume page offset.")] int pageOffset = 0,
        [Description("Page size (1-500).")] int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        var row = await _store.FindAsync(connectionId, cancellationToken);
        if (row is null) throw new KeyNotFoundException("connection not found");
        if (row.Provider != DatabaseProvider.Redis)
        {
            throw new InvalidOperationException(RedisRequirement.NotRedis(row, "key browsing"));
        }
        if (!RedisDatabaseIndex.TryCreate(database, out var dbIndex))
        {
            throw new InvalidOperationException(RedisDatabaseIndex.Refusal);
        }

        prefix ??= string.Empty;
        pageSize = Math.Clamp(pageSize, 1, 500);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var page = await _readService.ScanAsync(row, dbIndex, prefix, cursor, pageOffset, pageSize, cancellationToken, preferCache: false);
            sw.Stop();
            await _audit.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.Keys,
                Outcome = AuditOutcome.Success,
                Target = connectionId,
                Query = prefix,
                ResultCount = page.Nodes.Count + page.Keys.Count,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, cancellationToken);
            return page;
        }
        catch (Exception ex) when (ex is not NotSupportedException && ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Redis scan failed for {Id}", connectionId);
            throw new InvalidOperationException("redis scan failed");
        }
        catch (NotSupportedException)
        {
            // A cluster or sentinel is a policy refusal, not a failure: the
            // message says what to do, so it must reach the caller verbatim.
            sw.Stop();
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
    }
}