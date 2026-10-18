using System.ComponentModel;
using System.Diagnostics;
using Eling.Core.Audit;
using Eling.Core.Database;
using Eling.Core.Database.Adapters;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class DatabaseGetSchemaTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly ILogger<DatabaseGetSchemaTool>? _logger;
    private readonly IAuditLogger? _audit;

    public DatabaseGetSchemaTool(
        IDatabaseConnectionStore store,
        ILogger<DatabaseGetSchemaTool>? logger = null,
        IAuditLogger? audit = null)
    {
        _store = store;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "db_get_schema"), Description("Get schema (tables/columns/FKs/indexes) for a registered database connection.")]
    public async Task<DatabaseSchemaInfo> GetSchemaAsync(
        [Description("Connection id.")] string connectionId,
        [Description("Optional table name filter.")] string? tableName = null,
        CancellationToken cancellationToken = default)
    {
        var row = await _store.FindAsync(connectionId, cancellationToken);
        if (row is null)
        {
            throw new KeyNotFoundException("connection not found");
        }

        var adapter = DatabaseAdapterFactory.CreateAdapter(row);
        var sw = Stopwatch.StartNew();
        try
        {
            var schema = await adapter.GetSchemaAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                var filtered = schema.Tables.Where(t =>
                    string.Equals(t.Name, tableName, StringComparison.Ordinal)).ToList();
                schema = new DatabaseSchemaInfo(filtered);
            }
            sw.Stop();
            await (_audit?.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.GetSchema,
                Outcome = AuditOutcome.Success,
                Target = connectionId,
                Query = tableName,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, cancellationToken) ?? ValueTask.CompletedTask);
            return schema;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogWarning(ex, "Schema failed for {Id}", connectionId);
            await (_audit?.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.GetSchema,
                Outcome = AuditOutcome.Error,
                Target = connectionId,
                Query = tableName,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, cancellationToken) ?? ValueTask.CompletedTask);
            throw;
        }
    }
}
