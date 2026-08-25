using System.Text.Json.Serialization;

namespace Eling.Core;

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

/// <summary>
/// A registered runtime as tracked by the dashboard coordinator.
/// </summary>
public sealed class RuntimeInfo
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

    [JsonPropertyName("lastHeartbeat")]
    public DateTimeOffset LastHeartbeat { get; set; }

    [JsonPropertyName("isAlive")]
    public bool IsAlive { get; set; } = true;
}

// Source generator context for AOT-friendly serialization
[JsonSerializable(typeof(RuntimeRegistration))]
[JsonSerializable(typeof(List<RuntimeInfo>))]
public partial class CoordinatorJsonContext : JsonSerializerContext;
