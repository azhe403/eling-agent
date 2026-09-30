using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class DirectoryListAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "directory_list";

    public string Description => "List the contents of a directory in the workspace.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["path"],
      "properties": {
        "path": { "type": "string", "description": "Absolute or project-relative directory path." },
        "recursive": { "type": "boolean", "description": "Recurse into subdirectories." },
        "pattern": { "type": "string", "description": "Optional name filter like *.cs." },
        "maxDepth": { "type": "integer", "description": "Recursion depth cap." },
        "allowExternal": { "type": "boolean", "description": "Allow paths outside project root." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var path = root.TryGetProperty("path", out var pElem) ? pElem.GetString() ?? "" : "";
        var recursive = root.TryGetProperty("recursive", out var rElem) && rElem.GetBoolean();
        var pattern = root.TryGetProperty("pattern", out var ptElem) ? ptElem.GetString() : null;
        var maxDepth = root.TryGetProperty("maxDepth", out var mdElem) && mdElem.TryGetInt32(out var md) ? md : 1;
        var allowExternal = root.TryGetProperty("allowExternal", out var aeElem) && aeElem.GetBoolean();

        return Task.FromResult(tool.ListDirectory(path, recursive, maxDepth, pattern, allowExternal));
    }
}
