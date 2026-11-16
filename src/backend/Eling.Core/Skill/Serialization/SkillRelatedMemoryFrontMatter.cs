using YamlDotNet.Serialization;

namespace Eling.Core.Skill.Serialization;

/// <summary>
/// The DTO shape of a SKILL.md frontmatter <c>relatedMemories</c> entry.
/// </summary>
internal sealed class SkillRelatedMemoryFrontMatter
{
    [YamlMember(Alias = "id")]
    public string? Id { get; set; }

    [YamlMember(Alias = "scope")]
    public string? Scope { get; set; }

    [YamlMember(Alias = "relation")]
    public string? Relation { get; set; }
}
