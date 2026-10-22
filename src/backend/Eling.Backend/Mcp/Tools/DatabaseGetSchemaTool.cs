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

    [McpServerTool(Name = "db_get_schema"), Description("Get schema (tables/columns/FKs/indexes) for a registered database connection. A connection registered without a database is server-level: called without a database it returns the list of databases on the server (in Databases), and with a database it returns that database's tables.")]
    public async Task<DatabaseSchemaInfo> GetSchemaAsync(
        [Description("Connection id.")] string connectionId,
        [Description("Optional table name filter.")] string? tableName = null,
        [Description("Optional database name. Required for a server-level connection (one registered without a database) - call this without it first to see the databases.")] string? database = null,
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
            // A server-level connection - one registered without a database -
            // has no default schema, so it lists the databases on the server
            // instead. The names are identity, never credentials.
            var schema = string.IsNullOrWhiteSpace(database) && IsServerLevel(row)
                ? new DatabaseSchemaInfo([]) { Databases = await adapter.ListDatabasesAsync(cancellationToken) }
                : await adapter.GetSchemaAsync(database, cancellationToken);

            if (!string.IsNullOrWhiteSpace(tableName))
            {
                var filtered = schema.Tables.Where(t =>
                    string.Equals(t.Name, tableName, StringComparison.Ordinal)).ToList();
                schema = schema with { Tables = filtered };
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

            // The agent gets the category, never the provider's own words: a
            // MySQL failure reads "Access denied for user 'root'@'10.0.0.5'" and
            // names the account. The agent's boundary (the DB connection design
            // spec, section 4.2) forbids a credential in an error message as
            // firmly as it forbids one in a result.
            throw new InvalidOperationException("schema failed");
        }
    }

    /// <summary>
    /// True when the connection names no database - a server-level
    /// connection, which reaches every database on the server.
    /// </summary>
    private static bool IsServerLevel(DatabaseConnection connection) =>
        DatabaseConnectionStringFactory.IsServerLevel(connection.Provider, connection.ConnectionString);
}
