using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class CodebaseSearchAgentTool(CodebaseSearchTool tool) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => "codebase_search";

    public string Description => "FIRST move for locating code — call this before grep/glob/shell search. Ranks the FTS index (Porter+trigram, BM25) and returns path + line range + snippet ready to inject.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["query"],
      "properties": {
        "query": { "type": "string", "description": "Search query: words or a short concept from the code, e.g. 'tool registration' or 'AddElingMcpTools'." },
        "limit": { "type": "integer", "description": "Max hits (1-50, default 10)." },
        "pathPrefix": { "type": "string", "description": "Filter by path prefix, e.g. src/backend." },
        "filePattern": { "type": "string", "description": "Glob-ish file filter, e.g. *.cs or *.md." },
        "projectFilter": { "type": "string", "description": "Narrow search by workspace folder name." },
        "scope": { "type": "string", "description": "Scope: 'all' (default, every indexed workspace) or 'project' (this workspace only)." }
      }
    }
    """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var query = root.TryGetProperty("query", out var qElem) ? qElem.GetString() ?? "" : "";
        var limit = root.TryGetProperty("limit", out var lElem) && lElem.TryGetInt32(out var l) ? l : 10;
        var pathPrefix = root.TryGetProperty("pathPrefix", out var pElem) ? pElem.GetString() : null;
        var filePattern = root.TryGetProperty("filePattern", out var fElem) ? fElem.GetString() : null;
        var projectFilter = root.TryGetProperty("projectFilter", out var pfElem) ? pfElem.GetString() : null;
        var scope = root.TryGetProperty("scope", out var sElem) ? sElem.GetString() ?? "all" : "all";

        var response = await tool.SearchAsync(query, limit, pathPrefix, filePattern, projectFilter, scope, cancellationToken: ct);
        return JsonSerializer.Serialize(response, JsonOptions);
    }
}
