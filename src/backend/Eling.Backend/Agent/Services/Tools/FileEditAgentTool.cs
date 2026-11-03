using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class FileEditAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "file_edit";

    public string Description => "Surgically replace text in a file under the project root. oldString must match exactly once unless replaceAll=true. CRLF/LF differences are tolerated. Returns replacement count.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["path", "oldString", "newString"],
      "properties": {
        "path": { "type": "string", "description": "Absolute or project-relative file path." },
        "oldString": { "type": "string", "description": "Exact text to find. Include surrounding lines to make it unique." },
        "newString": { "type": "string", "description": "Replacement text." },
        "replaceAll": { "type": "boolean", "description": "Replace all occurrences instead of requiring a unique match. Defaults to false." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var path = root.TryGetProperty("path", out var pElem) ? pElem.GetString() ?? "" : "";
        var oldString = root.TryGetProperty("oldString", out var oElem) ? oElem.GetString() ?? "" : "";
        var newString = root.TryGetProperty("newString", out var nElem) ? nElem.GetString() ?? "" : "";
        var replaceAll = root.TryGetProperty("replaceAll", out var rElem) && rElem.GetBoolean();

        return Task.FromResult(tool.EditFile(path, oldString, newString, replaceAll));
    }
}
