using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class LogListAgentTool(LogTools tool) : IAgentTool
{
    public string Name => "log_list";

    public string Description => "List available diagnostic log files for a stream ('backend' or 'desktop').";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "stream": { "type": "string", "description": "Stream name: 'backend' (default) or 'desktop'." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var stream = root.TryGetProperty("stream", out var sElem) ? sElem.GetString() ?? "backend" : "backend";

        return Task.FromResult(tool.ListLogs(stream));
    }
}
