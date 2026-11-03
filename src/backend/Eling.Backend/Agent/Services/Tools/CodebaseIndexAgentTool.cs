using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class CodebaseIndexAgentTool(CodebaseIndexTool tool) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => "codebase_index";

    public string Description => "Build or refresh the codebase FTS index (incremental by default, cache in the global codebase store).";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "full": { "type": "boolean", "description": "true = rebuild all chunks, false = incremental by hash (default false)." },
        "paths": { "type": "array", "items": { "type": "string" }, "description": "Optional scope subfolders, e.g. [\"src/backend\"]. Null = whole workspace." }
      }
    }
    """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var full = root.TryGetProperty("full", out var fElem) && fElem.GetBoolean();
        string[]? paths = null;
        if (root.TryGetProperty("paths", out var pElem) && pElem.ValueKind == JsonValueKind.Array)
        {
            paths = JsonSerializer.Deserialize<string[]>(pElem.GetRawText(), JsonOptions);
        }

        var response = await tool.IndexAsync(full, paths, cancellationToken: ct);
        return JsonSerializer.Serialize(response, JsonOptions);
    }
}
