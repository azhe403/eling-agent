using System.Text.Json.Serialization;
using Eling.Core;

namespace Eling.Backend.Dtos;

/// <summary>
/// Recall summary counts surfaced to MCP clients.
/// </summary>
public sealed class MemoryRecallStatsDto
{
    [JsonPropertyName("totalMemories")]
    public int TotalMemories { get; set; }

    [JsonPropertyName("activeMemories")]
    public int ActiveMemories { get; set; }

    [JsonPropertyName("activeIntentions")]
    public int ActiveIntentions { get; set; }

    [JsonPropertyName("recallCount")]
    public int RecallCount { get; set; }

    [JsonPropertyName("recentCount")]
    public int RecentCount { get; set; }

    public static MemoryRecallStatsDto From(MemoryRecallStats stats) => new()
    {
        TotalMemories = stats.TotalMemories,
        ActiveMemories = stats.ActiveMemories,
        ActiveIntentions = stats.ActiveIntentions,
        RecallCount = stats.RecallCount,
        RecentCount = stats.RecentCount
    };
}
