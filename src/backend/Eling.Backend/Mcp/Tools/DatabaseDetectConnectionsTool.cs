using System.ComponentModel;
using System.Diagnostics;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class DatabaseDetectConnectionsTool
{
    private readonly AppSettingsConnectionScanner _scanner;
    private readonly ILogger<DatabaseDetectConnectionsTool>? _logger;
    private readonly IAuditLogger? _audit;

    public DatabaseDetectConnectionsTool(
        AppSettingsConnectionScanner scanner,
        ILogger<DatabaseDetectConnectionsTool>? logger = null,
        IAuditLogger? audit = null)
    {
        _scanner = scanner;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "db_detect_connections"), Description("Preview database connection strings found in the backend appsettings.json (passwords masked). Writes nothing.")]
    public async Task<IReadOnlyList<McpDetectedCandidate>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var detected = _scanner.Scan();
            var result = detected.Select((d, i) => new McpDetectedCandidate(
                i + 1,
                d.SettingsKey,
                d.Name,
                d.Provider,
                d.RejectionReason)).ToList();
            sw.Stop();
            await (_audit?.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.DetectConnections,
                Outcome = AuditOutcome.Success,
                ResultCount = detected.Count,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, cancellationToken) ?? ValueTask.CompletedTask);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await (_audit?.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.DetectConnections,
                Outcome = AuditOutcome.Error,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, cancellationToken) ?? ValueTask.CompletedTask);
            throw new InvalidOperationException($"detect failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }
}
