using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class FileWriteAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "file_write";

    public string Description => "Write a UTF-8 text file under the project root, creating parent directories on demand.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["path", "content"],
      "properties": {
        "path": { "type": "string", "description": "Absolute or project-relative file path." },
        "content": { "type": "string", "description": "File content (UTF-8)." },
        "overwrite": { "type": "boolean", "description": "Replace the file when it already exists." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var path = root.TryGetProperty("path", out var pElem) ? pElem.GetString() ?? "" : "";
        var content = root.TryGetProperty("content", out var cElem) ? cElem.GetString() ?? "" : "";
        var overwrite = root.TryGetProperty("overwrite", out var oElem) && oElem.GetBoolean();

        return Task.FromResult(tool.WriteFile(path, content, overwrite));
    }
}
