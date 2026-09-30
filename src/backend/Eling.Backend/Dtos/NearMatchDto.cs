using System.Collections.Generic;
using System.Text.Json.Serialization;
using Eling.Core;
using Eling.Core.Memory;

namespace Eling.Backend.Dtos;

/// <summary>
/// A near-duplicate of the memory being written, scored so the caller can decide
/// whether to keep, merge, or discard the new one.
/// </summary>
public sealed class NearMatchDto
{
    [JsonPropertyName("id")]
    public MemoryId Id { get; set; }

    [JsonPropertyName("contentPreview")]
    public string ContentPreview { get; set; } = "";

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("type")]
    public MemoryType Type { get; set; }

    [JsonPropertyName("tags")]
    public IReadOnlyCollection<string> Tags { get; set; } = [];

    public static NearMatchDto From(MemoryNearMatch match) => new()
    {
        Id = match.Id,
        ContentPreview = match.ContentPreview,
        Score = match.Score,
        Type = match.Type,
        Tags = match.Tags
    };
}
