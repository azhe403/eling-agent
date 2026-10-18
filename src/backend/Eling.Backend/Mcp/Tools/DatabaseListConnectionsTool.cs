using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class DatabaseListConnectionsTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly ILogger<DatabaseListConnectionsTool>? _logger;

    public DatabaseListConnectionsTool(
        IDatabaseConnectionStore store,
        ILogger<DatabaseListConnectionsTool>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    [McpServerTool(Name = "db_list_connections"), Description("List registered database connections (identity only, no credentials).")]
    public async Task<IReadOnlyList<DatabaseConnectionView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _store.ListAsync(cancellationToken);
        return rows.Select(c => new DatabaseConnectionView(
            c.Id,
            c.Name,
            c.Provider,
            c.Environment,
            !string.IsNullOrWhiteSpace(c.ConnectionString),
            c.AllowAgentMutations,
            c.RequireApprovalForMutations,
            c.CreatedAt,
            c.LastConnectedAt)).ToList();
    }
}
