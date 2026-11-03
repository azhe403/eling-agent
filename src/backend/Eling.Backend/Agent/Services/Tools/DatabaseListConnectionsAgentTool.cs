using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class DatabaseListConnectionsAgentTool(DatabaseListConnectionsTool tool) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => "db_list_connections";

    public string Description => "List registered database connections PLUS unimported connections found in appsettings*.json.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {}
    }
    """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var result = await tool.ListAsync(ct);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
