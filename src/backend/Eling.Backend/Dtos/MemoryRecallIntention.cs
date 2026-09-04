using System.Text.Json.Serialization;
using Eling.Core;

namespace Eling.Backend.Dtos;

/// <summary>
/// Lightweight intention descriptor exposed by <c>memory_recall</c>.
/// </summary>
public sealed class MemoryRecallIntention
{
    [JsonPropertyName("id")]
    public MemoryId Id { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("triggerType")]
    public string TriggerType { get; set; } = "";

    [JsonPropertyName("tags")]
    public IReadOnlyCollection<string> Tags { get; set; } = [];

    [JsonPropertyName("matched")]
    public bool Matched { get; set; }

    [JsonPropertyName("expired")]
    public bool Expired { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    public static MemoryRecallIntention From(MemoryRecallIntentionResult result) => new()
    {
        Id = result.Intention.Id,
        Description = result.Intention.Description,
        TriggerType = result.Intention.TriggerType.ToString(),
        Tags = result.Intention.Tags,
        Matched = result.Matched,
        Expired = result.Expired,
        UpdatedAt = result.Intention.UpdatedAt
    };
}
