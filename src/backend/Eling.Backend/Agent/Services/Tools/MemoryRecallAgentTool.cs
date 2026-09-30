using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Dtos;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class MemoryRecallAgentTool(MemoryRecallTool tool) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => "memory_recall";

    public string Description =>
        "Hydrate context on demand at any point in a conversation. Returns topic-relevant full memories (search-based recall), " +
        "the most recently updated active memories, outstanding intentions, project-scope posture, and lightweight stats.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "context": {
          "type": "object",
          "description": "Current task context (filePath, topics, project). Optional.",
          "properties": {
            "filePath": { "type": "string" },
            "project": { "type": "string" },
            "topics": { "type": "array", "items": { "type": "string" } }
          }
        },
        "recallLimit": { "type": "integer", "description": "Max recalled memories (default 10)." },
        "recentLimit": { "type": "integer", "description": "Max recent memories (default 10)." },
        "scope": { "type": "string", "description": "project | global | merged (default merged)." }
      }
    }
    """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        MemoryRecallContextInput? context = null;
        if (root.TryGetProperty("context", out var ctxElem) && ctxElem.ValueKind == JsonValueKind.Object)
        {
            context = JsonSerializer.Deserialize<MemoryRecallContextInput>(ctxElem.GetRawText(), JsonOptions);
        }

        var recallLimit = root.TryGetProperty("recallLimit", out var rlElem) && rlElem.TryGetInt32(out var rl) ? rl : 10;
        var recentLimit = root.TryGetProperty("recentLimit", out var rcElem) && rcElem.TryGetInt32(out var rc) ? rc : 10;
        var scope = root.TryGetProperty("scope", out var scElem) && scElem.ValueKind == JsonValueKind.String ? scElem.GetString() ?? "merged" : "merged";

        var result = await tool.RecallAsync(context, recallLimit, recentLimit, scope, ct);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
