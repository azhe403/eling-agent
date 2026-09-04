using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Root <c>memory_recall</c> response.
/// </summary>
public sealed class MemoryRecallResponse
{
    [JsonPropertyName("recallMemories")]
    public IReadOnlyCollection<MemoryRecallMemory> RecallMemories { get; set; } = [];

    [JsonPropertyName("recentMemories")]
    public IReadOnlyCollection<MemoryRecallMemory> RecentMemories { get; set; } = [];

    [JsonPropertyName("intentions")]
    public IReadOnlyCollection<MemoryRecallIntention> Intentions { get; set; } = [];

    [JsonPropertyName("stats")]
    public MemoryRecallStatsDto Stats { get; set; } = new();
}
