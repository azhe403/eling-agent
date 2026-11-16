using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Input shape for one entry in the skill_save relatedMemories /
/// relatedSnippets arrays: an id, the memory's scope label (omitted for
/// snippets), and the shared relation vocabulary term. The related-fragment
/// validation lives in the tool layer, which rejects unknown relation or
/// scope labels before anything touches disk.
/// </summary>
public sealed class SkillRelatedReferenceInput
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "";
}
