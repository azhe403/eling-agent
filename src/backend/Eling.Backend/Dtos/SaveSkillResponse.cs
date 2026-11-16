using System.Text.Json.Serialization;
using Eling.Core.Skill;

namespace Eling.Backend.Dtos;

/// <summary>
/// Envelope returned by the skill_save MCP tool: the identity of the skill that
/// was created or updated, plus the absolute path of its canonical SKILL.md.
/// </summary>
public sealed class SaveSkillResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "";

    public static SaveSkillResponse From(Skill skill, string path) => new()
    {
        Id = skill.Id.Value,
        Name = skill.Name,
        Path = path,
        Origin = skill.Origin.WireName
    };
}
