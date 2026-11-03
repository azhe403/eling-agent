using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class PathTestAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "path_test";

    public string Description => "Check whether a path exists and report its kind (file / directory / symlink) and size. Read-only; never throws for not-found.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["path"],
      "properties": {
        "path": { "type": "string", "description": "Absolute or project-relative path." },
        "allowExternal": { "type": "boolean", "description": "Allow paths outside project root (default true)." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var path = root.TryGetProperty("path", out var pElem) ? pElem.GetString() ?? "" : "";
        var allowExternal = !root.TryGetProperty("allowExternal", out var aElem) || aElem.GetBoolean();

        return Task.FromResult(tool.PathTest(path, allowExternal));
    }
}
