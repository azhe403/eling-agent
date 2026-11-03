using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class FileAppendAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "file_append";

    public string Description => "Append text to the end of a file under the project root, creating the file and parents when missing. Refuses binary files. Returns new size.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["path", "content"],
      "properties": {
        "path": { "type": "string", "description": "Absolute or project-relative file path." },
        "content": { "type": "string", "description": "Text to append (as-is, no newline added)." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var path = root.TryGetProperty("path", out var pElem) ? pElem.GetString() ?? "" : "";
        var content = root.TryGetProperty("content", out var cElem) ? cElem.GetString() ?? "" : "";

        return Task.FromResult(tool.AppendFile(path, content));
    }
}
