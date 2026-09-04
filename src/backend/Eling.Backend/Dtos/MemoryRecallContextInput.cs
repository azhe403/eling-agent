using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Task context passed to <c>memory_recall</c>. Mirrors the application
/// layer <c>MemoryRecallContext</c> but is decoupled so the MCP DTO can evolve
/// independently.
/// </summary>
public sealed class MemoryRecallContextInput
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("topics")]
    public string[]? Topics { get; set; }

    [JsonPropertyName("project")]
    public string? Project { get; set; }
}
