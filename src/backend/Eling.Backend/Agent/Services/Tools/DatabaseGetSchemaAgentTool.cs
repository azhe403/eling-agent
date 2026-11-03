using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class DatabaseGetSchemaAgentTool(DatabaseGetSchemaTool tool) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => "db_get_schema";

    public string Description => "Get schema (tables/columns/FKs/indexes) for a registered database connection.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["connectionId"],
      "properties": {
        "connectionId": { "type": "string", "description": "Connection id." },
        "tableName": { "type": "string", "description": "Optional table name filter." },
        "database": { "type": "string", "description": "Optional database name for server-level connection." }
      }
    }
    """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var connectionId = root.TryGetProperty("connectionId", out var cElem) ? cElem.GetString() ?? "" : "";
        var tableName = root.TryGetProperty("tableName", out var tElem) ? tElem.GetString() : null;
        var database = root.TryGetProperty("database", out var dElem) ? dElem.GetString() : null;

        var result = await tool.GetSchemaAsync(connectionId, tableName, database, ct);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
