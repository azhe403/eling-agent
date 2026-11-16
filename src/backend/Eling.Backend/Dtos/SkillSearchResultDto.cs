using System.Text.Json.Serialization;
using Eling.Core.Skill;

namespace Eling.Backend.Dtos;

/// <summary>
/// One ranked hit from skill_search. Score is the BM25 score with the
/// sign inverted (higher is a better match), origin is the wire spelling
/// "eling" or "external", and Path is the absolute SKILL.md location.
/// </summary>
public sealed class SkillSearchResultDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "";

    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>
    /// Builds the DTO straight from the index hit — no catalog hydration
    /// required. The hit carries id, name, description, absolute path, origin
    /// and score (denormalized by the search query).
    /// </summary>
    public static SkillSearchResultDto From(SkillSearchResult hit) => new()
    {
        Id = hit.Id.Value,
        Name = hit.Name,
        Description = hit.Description,
        Path = hit.Path,
        Origin = hit.Origin.WireName,
        Score = hit.Rank
    };
}
