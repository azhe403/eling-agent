using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class DatabaseQueryAgentTool(DatabaseQueryTool tool) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => "db_query";

    public string Description => "Run a read-only SQL query (SELECT/EXPLAIN/PRAGMA only) against a registered database connection.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["connectionId", "sql"],
      "properties": {
        "connectionId": { "type": "string", "description": "Connection id." },
        "sql": { "type": "string", "description": "SQL statement (SELECT/EXPLAIN/PRAGMA only)." },
        "maxRows": { "type": "integer", "description": "Max rows (default 100)." },
        "database": { "type": "string", "description": "Optional database name for server-level connection." }
      }
    }
    """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var connectionId = root.TryGetProperty("connectionId", out var cElem) ? cElem.GetString() ?? "" : "";
        var sql = root.TryGetProperty("sql", out var sElem) ? sElem.GetString() ?? "" : "";
        var maxRows = root.TryGetProperty("maxRows", out var mElem) && mElem.TryGetInt32(out var m) ? m : 100;
        var database = root.TryGetProperty("database", out var dElem) ? dElem.GetString() : null;

        var result = await tool.QueryAsync(connectionId, sql, maxRows, database, ct);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
