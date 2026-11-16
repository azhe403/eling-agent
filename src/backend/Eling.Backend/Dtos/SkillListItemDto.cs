using System.Text.Json.Serialization;
using Eling.Core.Skill;

namespace Eling.Backend.Dtos;

/// <summary>
/// Compact item shape for the skill_list envelope: enough to pick a skill
/// out of the page without inlining its body. Path is the absolute
/// SKILL.md location; for trashed rows it points at the former active
/// location.
/// </summary>
public sealed class SkillListItemDto
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

    [JsonPropertyName("tags")]
    public IReadOnlyCollection<string> Tags { get; set; } = [];

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    public static SkillListItemDto From(Skill skill, string path) => new()
    {
        Id = skill.Id.Value,
        Name = skill.Name,
        Description = skill.Description,
        Path = path,
        Origin = skill.Origin.WireName,
        Tags = skill.Tags,
        UpdatedAt = skill.UpdatedAt
    };
}
