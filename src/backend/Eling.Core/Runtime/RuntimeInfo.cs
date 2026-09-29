using System.Text.Json.Serialization;

namespace Eling.Core.Runtime;

/// <summary>
/// A registered runtime as tracked by the dashboard coordinator.
/// </summary>
public sealed class RuntimeInfo
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("projectRoot")]
    public string ProjectRoot { get; set; } = "";

    /// <summary>
    /// Working directory the backend was launched in — the codebase index
    /// root. Falls back to <see cref="ProjectRoot"/> for registrations
    /// written by older binaries that predate the field.
    /// </summary>
    [JsonPropertyName("workspaceRoot")]
    public string WorkspaceRoot { get; set; } = "";

    /// <summary>
    /// False when this runtime opted out of codebase indexing: hide it from
    /// the codebase dropdown and the "all" scope. True when omitted (older
    /// binaries predate the flag and always participate).
    /// </summary>
    [JsonPropertyName("codebaseEnabled")]
    public bool CodebaseEnabled { get; set; } = true;

    /// <summary>
    /// The root codebase reads (search, top files, tiles) federate over:
    /// the workspace when known, otherwise the memory project root.
    /// Single home so REST and MCP resolve "all" scope identically.
    /// </summary>
    public string CodebaseRoot() =>
        string.IsNullOrWhiteSpace(WorkspaceRoot) ? ProjectRoot : WorkspaceRoot;

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
