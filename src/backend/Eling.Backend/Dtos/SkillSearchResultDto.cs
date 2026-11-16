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

    public static SkillSearchResultDto From(Skill skill, SkillSearchResult hit) => new()
    {
        Id = skill.Id.Value,
        Name = skill.Name,
        Description = skill.Description,
        Path = hit.Path,
        Origin = hit.Origin.WireName,
        Score = hit.Rank
    };
}
