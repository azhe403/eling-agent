using System.Text.Json.Serialization;

namespace Eling.Core.Runtime;

/// <summary>
/// Payload an eling runtime sends when registering with the shared dashboard.
/// </summary>
public sealed class RuntimeRegistration
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("projectRoot")]
    public string ProjectRoot { get; set; } = "";

    [JsonPropertyName("dataDirectory")]
    public string DataDirectory { get; set; } = "";

    [JsonPropertyName("startTime")]
    public DateTimeOffset StartTime { get; set; }

    [JsonPropertyName("mcpEnabled")]
    public bool McpEnabled { get; set; }

    [JsonPropertyName("mcpTransport")]
    public string McpTransport { get; set; } = "";
}
