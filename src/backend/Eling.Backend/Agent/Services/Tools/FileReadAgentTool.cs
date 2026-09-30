using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class FileReadAgentTool(FileSystemTools tool) : IAgentTool
{
    public string Name => "file_read";

    public string Description => "Read a text file within the workspace. Returns line-numbered text or JSON envelope if offset/limit specified.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["path"],
      "properties": {
        "path": { "type": "string", "description": "Absolute or project-relative file path." },
        "offset": { "type": "integer", "description": "First line to return (1-based)." },
        "limit": { "type": "integer", "description": "Max lines to return (0 = to end of file)." },
        "lineNumbers": { "type": "boolean", "description": "Prefix each returned line with its line number." },
        "allowExternal": { "type": "boolean", "description": "Allow paths outside the project root." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var path = root.TryGetProperty("path", out var pElem) ? pElem.GetString() ?? "" : "";
        var offset = root.TryGetProperty("offset", out var oElem) && oElem.TryGetInt32(out var o) ? o : 1;
        var limit = root.TryGetProperty("limit", out var lElem) && lElem.TryGetInt32(out var l) ? l : 0;
        var lineNumbers = root.TryGetProperty("lineNumbers", out var lnElem) && lnElem.GetBoolean();
        var allowExternal = root.TryGetProperty("allowExternal", out var aeElem) && aeElem.GetBoolean();

        return Task.FromResult(tool.ReadFile(path, 1048576, offset, limit, allowExternal, lineNumbers));
    }
}
