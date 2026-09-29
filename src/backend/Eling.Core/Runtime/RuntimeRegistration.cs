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

    /// <summary>
    /// Working directory the backend was launched in — the codebase index
    /// root and the file-search workspace. Memory identity stays in
    /// <see cref="ProjectRoot"/> (nearest <c>.eling</c> ancestor); the two
    /// differ when a subfolder (or a folder without <c>.eling</c>) is opened
    /// as the workspace. Empty for registrations written by older binaries.
    /// </summary>
    [JsonPropertyName("workspaceRoot")]
    public string WorkspaceRoot { get; set; } = "";

    /// <summary>
    /// False when this runtime opted out of codebase indexing (temporal
    /// workspace, see <c>ELING_CODEBASE_EXCLUDE</c>): it owns no index DB,
    /// runs no watcher, and must be hidden from the codebase dropdown and
    /// the "all" scope. True when omitted (older binaries predate the flag
    /// and always participate).
    /// </summary>
    [JsonPropertyName("codebaseEnabled")]
    public bool CodebaseEnabled { get; set; } = true;

    [JsonPropertyName("dataDirectory")]
    public string DataDirectory { get; set; } = "";

    [JsonPropertyName("startTime")]
    public DateTimeOffset StartTime { get; set; }

    [JsonPropertyName("mcpEnabled")]
    public bool McpEnabled { get; set; }

    [JsonPropertyName("mcpTransport")]
    public string McpTransport { get; set; } = "";
}
