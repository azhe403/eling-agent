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

    /// <summary>
    /// Digest of relevant hits beyond the recall limit. Present only when
    /// there is overflow to summarize, so quiet payloads stay lean.
    /// </summary>
    [JsonPropertyName("recallSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecallSummaryDto? RecallSummary { get; set; }

    /// <summary>
    /// Topic-independent baseline of standing rules and preferences, standing
    /// decisions, and lessons. Returned on every call — even a narrow topic
    /// call still carries this consistency spine.
    /// </summary>
    [JsonPropertyName("standingMemories")]
    public IReadOnlyCollection<MemoryRecallMemory> StandingMemories { get; set; } = [];

    [JsonPropertyName("intentions")]
    public IReadOnlyCollection<MemoryRecallIntention> Intentions { get; set; } = [];

    [JsonPropertyName("stats")]
    public MemoryRecallStatsDto Stats { get; set; } = new();

    [JsonPropertyName("projectScope")]
    public MemoryRecallProjectScopeDto ProjectScope { get; set; } = new();

    /// <summary>
    /// Codebase index status for the current workspace. Included so agents can
    /// verify index readiness at session start without a separate tool call.
    /// </summary>
    [JsonPropertyName("codebase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodebaseStatusResponse? Codebase { get; set; }

    /// <summary>
    /// Update notice, serialized only when an update is available so quiet
    /// payloads stay lean.
    /// </summary>
    [JsonPropertyName("update")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UpdateNoticeDto? Update { get; set; }
}
