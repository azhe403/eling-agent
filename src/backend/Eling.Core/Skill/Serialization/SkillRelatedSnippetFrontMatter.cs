using YamlDotNet.Serialization;

namespace Eling.Core.Skill.Serialization;

/// <summary>
/// The DTO shape of a SKILL.md frontmatter <c>relatedSnippets</c> entry.
/// </summary>
internal sealed class SkillRelatedSnippetFrontMatter
{
    [YamlMember(Alias = "id")]
    public string? Id { get; set; }

    /// <summary>
    /// Forbidden key, kept as a sentinel so a snippet reference that
    /// nonetheless carries a scope is rejected loudly instead of being
    /// silently swallowed by <c>IgnoreUnmatchedProperties</c>.
    /// </summary>
    [YamlMember(Alias = "scope")]
    public string? Scope { get; set; }

    [YamlMember(Alias = "relation")]
    public string? Relation { get; set; }
}
