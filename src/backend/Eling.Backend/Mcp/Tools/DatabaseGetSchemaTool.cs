using System.ComponentModel;
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

    public DatabaseGetSchemaTool(
        IDatabaseConnectionStore store,
        ILogger<DatabaseGetSchemaTool>? logger = null)
    {
        _store = store;
        _logger = logger;
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
        var schema = await adapter.GetSchemaAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            var filtered = schema.Tables.Where(t =>
                string.Equals(t.Name, tableName, StringComparison.Ordinal)).ToList();
            return new DatabaseSchemaInfo(filtered);
        }
        return schema;
    }
}
