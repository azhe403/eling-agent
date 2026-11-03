using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Agent.Services.Tools;

public sealed class LogReadAgentTool(LogTools tool) : IAgentTool
{
    public string Name => "log_read";

    public string Description => "Read diagnostic log entries from a stream ('backend' or 'desktop') with level, substring, and tail filters.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "stream": { "type": "string", "description": "Stream: 'backend' (default) or 'desktop'." },
        "date": { "type": "string", "description": "Optional archive date: YYYY-MM-DD." },
        "level": { "type": "string", "description": "Level filter: DEBUG, INFO, WARN, ERROR, FATAL." },
        "filter": { "type": "string", "description": "Case-insensitive substring filter." },
        "tail": { "type": "integer", "description": "Max entries from end (1-5000, default 100)." }
      }
    }
    """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var stream = root.TryGetProperty("stream", out var sElem) ? sElem.GetString() ?? "backend" : "backend";
        var date = root.TryGetProperty("date", out var dElem) ? dElem.GetString() : null;
        var level = root.TryGetProperty("level", out var lElem) ? lElem.GetString() : null;
        var filter = root.TryGetProperty("filter", out var fElem) ? fElem.GetString() : null;
        var tail = root.TryGetProperty("tail", out var tElem) && tElem.TryGetInt32(out var t) ? t : 100;

        return Task.FromResult(tool.ReadLog(stream, date, tail, level, filter));
    }
}
