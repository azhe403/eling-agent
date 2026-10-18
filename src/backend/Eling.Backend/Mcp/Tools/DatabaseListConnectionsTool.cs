using System.ComponentModel;
using System.Diagnostics;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class DatabaseListConnectionsTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly AppSettingsConnectionScanner? _scanner;
    private readonly ILogger<DatabaseListConnectionsTool>? _logger;
    private readonly IAuditLogger? _audit;

    public DatabaseListConnectionsTool(
        IDatabaseConnectionStore store,
        AppSettingsConnectionScanner? scanner = null,
        ILogger<DatabaseListConnectionsTool>? logger = null,
        IAuditLogger? audit = null)
    {
        _store = store;
        _scanner = scanner;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "db_list_connections"), Description("List registered database connections (identity only, no credentials) PLUS any connections found in appsettings.json that are not yet registered (Unimported). Run db_import_connections for the approved unimported keys to register them. Detect is read-only and runs automatically here so unimported connections are never missed.")]
    public async Task<McpConnectionsOverview> ListAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var rows = await _store.ListAsync(cancellationToken);
        var registered = rows.Select(c => new DatabaseConnectionView(
            c.Id,
            c.Name,
            c.Provider,
            c.Environment,
            !string.IsNullOrWhiteSpace(c.ConnectionString),
            c.AllowAgentMutations,
            c.RequireApprovalForMutations,
            c.CreatedAt,
            c.LastConnectedAt)).ToList();

        var unimported = new List<McpDetectedCandidate>();
        if (_scanner is not null)
        {
            try
            {
                var detected = _scanner.Scan();
                var registeredNames = new HashSet<string>(rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < detected.Count; i++)
                {
                    var d = detected[i];
                    if (registeredNames.Contains(d.Name)) continue;
                    unimported.Add(new McpDetectedCandidate(
                        i + 1,
                        d.SettingsKey,
                        d.Name,
                        d.Provider,
                        d.RejectionReason));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Detect failed while listing connections");
            }
        }

        sw.Stop();
        await (_audit?.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Access,
            Category = AuditCategory.Database,
            Action = DatabaseAuditActions.ListConnections,
            Outcome = AuditOutcome.Success,
            ResultCount = rows.Count,
            DurationMs = (int)sw.ElapsedMilliseconds
        }, cancellationToken) ?? ValueTask.CompletedTask);

        return new McpConnectionsOverview(registered, unimported);
    }
}