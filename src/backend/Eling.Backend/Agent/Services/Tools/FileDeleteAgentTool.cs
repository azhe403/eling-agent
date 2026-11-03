using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class FileDeleteAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "file_delete";

    public string Description => "Permanently delete a file under the project root. The sandbox root itself can never be deleted.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["path"],
      "properties": {
        "path": { "type": "string", "description": "Absolute or project-relative file path." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var path = root.TryGetProperty("path", out var pElem) ? pElem.GetString() ?? "" : "";

        return Task.FromResult(tool.DeleteFile(path));
    }
}
