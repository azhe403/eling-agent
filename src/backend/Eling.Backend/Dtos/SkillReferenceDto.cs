using System.Text.Json.Serialization;
using Eling.Core.Recall;
using Eling.Core.Skill;

namespace Eling.Backend.Dtos;

/// <summary>
/// One skill referenced by the recall response. Carries only what a
/// caller needs to decide whether to read the skill: name, one-line
/// description, origin ("eling"/"external"), and the absolute SKILL.md
/// path. Bodies are never inlined - read the path to load the skill.
/// </summary>
public sealed class SkillReferenceDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    public static SkillReferenceDto From(SkillRecallHit hit) => new()
    {
        Name = hit.Name,
        Description = hit.Description,
        Origin = hit.Origin.WireName,
        Path = hit.Path
    };
}
