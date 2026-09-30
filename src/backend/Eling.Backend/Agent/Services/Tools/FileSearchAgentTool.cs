using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class FileSearchAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "file_search";

    public string Description => "Search file contents under a base path for literal text or regular expressions.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["basePath", "pattern"],
      "properties": {
        "basePath": { "type": "string", "description": "Root to search under." },
        "pattern": { "type": "string", "description": "Substring or regex pattern." },
        "useRegex": { "type": "boolean", "description": "Interpret pattern as regex." },
        "caseSensitive": { "type": "boolean", "description": "Case-sensitive matching." },
        "filePattern": { "type": "string", "description": "Optional name filter like *.cs." },
        "maxDepth": { "type": "integer", "description": "Recursion depth cap." },
        "maxResults": { "type": "integer", "description": "Max hits returned." },
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
        var useRegex = root.TryGetProperty("useRegex", out var rElem) && rElem.GetBoolean();
        var caseSensitive = root.TryGetProperty("caseSensitive", out var cElem) && cElem.GetBoolean();
        var filePattern = root.TryGetProperty("filePattern", out var fpElem) ? fpElem.GetString() : null;
        var maxDepth = root.TryGetProperty("maxDepth", out var mdElem) && mdElem.TryGetInt32(out var md) ? md : 5;
        var maxResults = root.TryGetProperty("maxResults", out var mrElem) && mrElem.TryGetInt32(out var mr) ? mr : 100;
        var allowExternal = root.TryGetProperty("allowExternal", out var aeElem) && aeElem.GetBoolean();

        return Task.FromResult(tool.SearchFiles(basePath, pattern, useRegex, caseSensitive, filePattern, maxDepth, maxResults, 1048576, allowExternal));
    }
}
