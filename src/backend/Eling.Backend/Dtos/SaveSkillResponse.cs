using System.Text.Json.Serialization;
using Eling.Core.Skill;

namespace Eling.Backend.Dtos;

/// <summary>
/// Envelope returned by the skill_save MCP tool: the identity of the skill that
/// was created or updated, plus the absolute path of its canonical SKILL.md.
/// </summary>
public sealed class SaveSkillResponse
{
    /// <summary>
    /// "created" when no index row existed for the name; "updated" when an
    /// existing Eling-managed skill was replaced. Lowercase, matching
    /// memory_save's <c>action</c> wire spelling.
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "";

    public static SaveSkillResponse From(SkillSaveResult result, string path) => new()
    {
        Action = result.Action == SkillSaveAction.Created ? "created" : "updated",
        Id = result.Skill.Id.Value,
        Name = result.Skill.Name,
        Path = path,
        Origin = result.Skill.Origin.WireName
    };
}
