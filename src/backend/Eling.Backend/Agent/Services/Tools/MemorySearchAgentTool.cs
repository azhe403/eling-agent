using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class MemorySearchAgentTool(MemoryReadTool tool) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => "memory_search";

    public string Description => "Search memories by keyword query.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["query"],
      "properties": {
        "query": { "type": "string", "description": "The search query" },
        "limit": { "type": "integer", "description": "Maximum number of results to return. Defaults to 10." },
        "scope": { "type": "string", "description": "Scope: project, global, or merged. Defaults to merged." }
      }
    }
    """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var query = root.TryGetProperty("query", out var qElem) ? qElem.GetString() ?? "" : "";
        var limit = root.TryGetProperty("limit", out var lElem) && lElem.TryGetInt32(out var l) ? l : 10;
        var scope = root.TryGetProperty("scope", out var sElem) ? sElem.GetString() ?? "merged" : "merged";

        var results = await tool.SearchAsync(query, limit, scope);
        return JsonSerializer.Serialize(results, JsonOptions);
    }
}
