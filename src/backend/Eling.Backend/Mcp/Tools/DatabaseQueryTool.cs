using System.ComponentModel;
using Eling.Core.Database;
using Eling.Core.Database.Adapters;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class DatabaseQueryTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly ILogger<DatabaseQueryTool>? _logger;

    public DatabaseQueryTool(
        IDatabaseConnectionStore store,
        ILogger<DatabaseQueryTool>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    [McpServerTool(Name = "db_query"), Description("Run a read-only SQL query (SELECT/EXPLAIN/PRAGMA only) against a registered database connection.")]
    public async Task<QueryResult> QueryAsync(
        [Description("Connection id.")] string connectionId,
        [Description("SQL statement.")] string sql,
        [Description("Max rows (default 100).")] int maxRows = 100,
        CancellationToken cancellationToken = default)
    {
        var row = await _store.FindAsync(connectionId, cancellationToken);
        if (row is null)
        {
            throw new KeyNotFoundException("connection not found");
        }

        var classification = SqlSafety.Classify(sql);
        if (classification.Verdict != SqlVerdict.ReadOnly)
        {
            throw new InvalidOperationException(classification.Reason ?? "rejected");
        }

        var adapter = DatabaseAdapterFactory.CreateAdapter(row);
        try
        {
            if (maxRows < 1) maxRows = 1;
            if (maxRows > 1000) maxRows = 1000;
            return await adapter.QueryAsync(sql, maxRows, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Query failed for {Id}", connectionId);
            throw new InvalidOperationException("query failed");
        }
    }
}
