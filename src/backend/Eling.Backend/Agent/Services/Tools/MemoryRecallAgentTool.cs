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
        "Hydrate context on demand at any point in a conversation. On the first user turn of a session and on every topic/step transition, call this with a BROAD topic set — the current message's topics plus standing rules, preferences, decisions, lessons and known gotchas, workflow facts, and recently touched work — passed as context: { topics: [...] } or the top-level topics alias. Every response also carries standingMemories, a topic-independent baseline of standing rules/preferences, standing decisions, and lessons; read it even when your topic call was narrow. When topic recall exceeds the limit, the response also carries recallSummary — digest paragraphs of the overflowing items (source tells judge from heuristic; pass summarizeOverflow: false to skip). If stats.recallCount comes back 0, the call was mis-shaped or matched nothing: re-issue before answering. Returns topic-relevant full memories (search-based recall), the most recently updated active memories, the standing baseline, the overflow digest, outstanding intentions, project-scope posture, codebase status, and lightweight stats.";

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
        "scope": { "type": "string", "description": "project | global | merged (default merged)." },
        "topics": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Alias for context.topics: a top-level topic array. When non-empty, these topics replace context.topics."
        },
        "summarizeOverflow": {
          "type": "boolean",
          "description": "Summarize recall items beyond recallLimit into recallSummary (default true). Set false to skip for minimum latency."
        }
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

        string[]? topics = null;
        if (root.TryGetProperty("topics", out var tElem) && tElem.ValueKind == JsonValueKind.Array)
        {
            topics = tElem.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())
                .OfType<string>()
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
        }

        var summarizeOverflow = !root.TryGetProperty("summarizeOverflow", out var sElem)
            || sElem.ValueKind != JsonValueKind.False;

        var result = await tool.RecallAsync(context, recallLimit, recentLimit, scope, topics, summarizeOverflow, ct);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
