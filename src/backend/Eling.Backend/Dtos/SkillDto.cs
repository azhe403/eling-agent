using System.Text.Json.Serialization;
using Eling.Core.Skill;

namespace Eling.Backend.Dtos;

/// <summary>
/// Full read view of a skill: frontmatter-equivalent fields, the body, the
/// absolute SKILL.md path and the origin marker. Returned by skill_get,
/// skill_restore, and (compactly, via SkillListItemDto) skill_list.
/// </summary>
public sealed class SkillDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "";

    [JsonPropertyName("tags")]
    public IReadOnlyCollection<string> Tags { get; set; } = [];

    [JsonPropertyName("relatedMemories")]
    public IReadOnlyCollection<SkillRelatedReferenceDto> RelatedMemories { get; set; } = [];

    [JsonPropertyName("relatedSnippets")]
    public IReadOnlyCollection<SkillRelatedReferenceDto> RelatedSnippets { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    public static SkillDto From(Skill skill, string path) => new()
    {
        Id = skill.Id.Value,
        Name = skill.Name,
        Description = skill.Description,
        Body = skill.Body,
        Path = path,
        Origin = skill.Origin.WireName,
        Tags = skill.Tags,
        RelatedMemories = skill.RelatedMemories
            .Select(r => new SkillRelatedReferenceDto { Id = r.Id, Scope = r.Scope, Relation = r.Relation })
            .ToList(),
        RelatedSnippets = skill.RelatedSnippets
            .Select(r => new SkillRelatedReferenceDto { Id = r.Id, Relation = r.Relation })
            .ToList(),
        CreatedAt = skill.CreatedAt,
        UpdatedAt = skill.UpdatedAt
    };
}
