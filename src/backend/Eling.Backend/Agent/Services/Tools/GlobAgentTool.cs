using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class GlobAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "glob";

    public string Description => "Search for files and directories under a base path matching a glob pattern.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["basePath", "pattern"],
      "properties": {
        "basePath": { "type": "string", "description": "Root path to search under." },
        "pattern": { "type": "string", "description": "Glob pattern, e.g. **/*.cs" },
        "maxDepth": { "type": "integer", "description": "Recursion depth cap (0-10)." },
        "maxResults": { "type": "integer", "description": "Max entries returned (1-1000)." },
        "allowExternal": { "type": "boolean", "description": "Allow paths outside root." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var basePath = root.TryGetProperty("basePath", out var bElem) ? bElem.GetString() ?? "" : "";
        var pattern = root.TryGetProperty("pattern", out var pElem) ? pElem.GetString() ?? "" : "";
        var maxDepth = root.TryGetProperty("maxDepth", out var mdElem) && mdElem.TryGetInt32(out var md) ? md : 5;
        var maxResults = root.TryGetProperty("maxResults", out var mrElem) && mrElem.TryGetInt32(out var mr) ? mr : 100;
        var allowExternal = root.TryGetProperty("allowExternal", out var aeElem) && aeElem.GetBoolean();

        return Task.FromResult(tool.Glob(basePath, pattern, maxDepth, maxResults, allowExternal));
    }
}
