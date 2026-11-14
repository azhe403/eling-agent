using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Memory;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class MemorySaveAgentTool(MemoryWriteTool tool) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => "memory_save";

    public string Description =>
        "Save a memory to the knowledge store. Call this proactively whenever the turn contains something meant to outlive this conversation — either something the user states as durable (a standing rule or preference, a correction, a decision and its rationale, an explicit 'remember this') or a project/environment fact you observed while working that the tree does not already record. Do not wait for the words 'remember' or 'ingat', and never substitute a plain-text acknowledgement ('okay', 'noted', 'siap', 'sure') for the call: acknowledging without saving is a failure, and the acknowledgement and the save are additive. Skip transient task state, speculation, unverified claims, and anything already recorded in AGENTS.md, code, specs, or git history. Content is the main text to remember, with optional tags, type, and source. Scope defaults to 'project-local' (machine-only, never committed) — pass scope explicitly on every save: 'global' only for knowledge that holds in a different, unrelated project; 'project' (committed, shared via git) ONLY when the user explicitly wants this memory shared for this repo. The content MUST carry the intent — the durable rule, decision, fact, and its reason — never a fragment of the user's message: no quotes or lightly reworded copies of their words, no narration of the exchange, no diary-style retell. Stranger test before saving: could a future session apply this memory without knowing this conversation? If not, rewrite it as the convention itself; if the only substance is the quote, do not save at all.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["content"],
      "properties": {
        "content": { "type": "string", "description": "The content to remember — the durable rule, decision, fact and why, standalone; never a fragment of the user's message (no quotes, no exchange narration)" },
        "tags": { "type": "array", "items": { "type": "string" }, "description": "Optional tags for categorization" },
        "type": { "type": "string", "description": "Type of memory: fact, preference, decision, lesson, note. Defaults to fact." },
        "source": { "type": "string", "description": "Optional source reference" },
        "scope": { "type": "string", "description": "Scope: project | project-local | global. Defaults to project-local (machine-only, never committed); pass explicitly. Use global only for cross-project knowledge; use project only when the user explicitly wants this memory committed and shared via git." },
        "project": { "type": "string", "description": "Optional logical name of an ancestor project." }
      }
    }
    """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var content = root.TryGetProperty("content", out var cElem) ? cElem.GetString() ?? "" : "";
        string[]? tags = null;
        if (root.TryGetProperty("tags", out var tElem) && tElem.ValueKind == JsonValueKind.Array)
        {
            tags = JsonSerializer.Deserialize<string[]>(tElem.GetRawText(), JsonOptions);
        }

        var type = root.TryGetProperty("type", out var tyElem) ? tyElem.GetString() ?? "fact" : "fact";
        var source = root.TryGetProperty("source", out var sElem) ? sElem.GetString() : null;
        var scope = MemoryScopeLabels.DefaultSaveScope;
        if (root.TryGetProperty("scope", out var scElem))
        {
            scope = scElem.GetString() ?? scope;
        }
        var project = root.TryGetProperty("project", out var pElem) ? pElem.GetString() : null;

        var memory = await tool.SaveAsync(content, type, tags, source, scope, project);
        return JsonSerializer.Serialize(memory, JsonOptions);
    }
}
