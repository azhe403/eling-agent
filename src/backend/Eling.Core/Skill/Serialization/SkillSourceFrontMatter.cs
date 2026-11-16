using YamlDotNet.Serialization;

namespace Eling.Core.Skill.Serialization;

/// <summary>
/// The DTO shape of a SKILL.md frontmatter <c>source</c> block.
/// </summary>
internal sealed class SkillSourceFrontMatter
{
    [YamlMember(Alias = "upstream")]
    public string? Upstream { get; set; }

    [YamlMember(Alias = "tracked")]
    public bool Tracked { get; set; }
}
