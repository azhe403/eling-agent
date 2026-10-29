using System.ComponentModel;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class RedisInspectTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly IRedisKeyReadService _readService;
    private readonly ILogger<RedisInspectTool> _logger;
    private readonly IAuditLogger _audit;

    public RedisInspectTool(
        IDatabaseConnectionStore store,
        IRedisKeyReadService readService,
        ILogger<RedisInspectTool> logger,
        IAuditLogger audit)
    {
        _store = store;
        _readService = readService;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "redis_inspect"), Description("Inspect a single Redis key: type, TTL, length, encoding, and a capped value preview (max 100 entries / 64 KB).")]
    public async Task<KeyInspectResult> InspectAsync(
        [Description("Connection id.")] string connectionId,
        [Description("Key to inspect.")] string key,
        [Description("Logical database index (0-15).")] int database = 0,
        CancellationToken cancellationToken = default)
    {
        var row = await _store.FindAsync(connectionId, cancellationToken);
        if (row is null) throw new KeyNotFoundException("connection not found");
        if (row.Provider != DatabaseProvider.Redis)
        {
            throw new InvalidOperationException(RedisRequirement.NotRedis(row, "key browsing"));
        }
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key must not be blank");
        if (!RedisDatabaseIndex.TryCreate(database, out var dbIndex))
        {
            throw new ArgumentException(RedisDatabaseIndex.Refusal);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _readService.InspectAsync(row, dbIndex, key, cancellationToken);
            sw.Stop();
            await _audit.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.InspectKey,
                Outcome = AuditOutcome.Success,
                Target = connectionId,
                Query = key,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, cancellationToken);
            return result;
        }
        catch (KeyNotFoundException)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex) when (ex is not NotSupportedException && ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Redis inspect failed for {Id}", connectionId);
            throw new InvalidOperationException("redis inspect failed");
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