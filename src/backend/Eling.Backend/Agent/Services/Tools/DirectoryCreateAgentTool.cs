using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class DirectoryCreateAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "directory_create";

    public string Description => "Create a directory under the project root (and any missing parents), idempotent like mkdir -p.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["path"],
      "properties": {
        "path": { "type": "string", "description": "Absolute or project-relative directory path." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var path = root.TryGetProperty("path", out var pElem) ? pElem.GetString() ?? "" : "";

        return Task.FromResult(tool.CreateDirectory(path));
    }
}
