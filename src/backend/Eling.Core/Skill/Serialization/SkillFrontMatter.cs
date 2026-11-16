using YamlDotNet.Serialization;

namespace Eling.Core.Skill.Serialization;

/// <summary>
/// The DTO shape of a SKILL.md frontmatter block, matching spec §3.2 exactly:
/// camelCase keys, <c>relatedMemories</c> items carry <c>{ id, scope, relation }</c>,
/// <c>relatedSnippets</c> items carry <c>{ id, relation }</c> (no scope). No
/// naming convention is registered (SkillFileFormat verifies none is wanted: a
/// convention would rewrite the aliases), so every member is pinned with an
/// explicit camelCase alias and the wire keys are stable regardless.
/// </summary>
internal sealed class SkillFrontMatter
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = string.Empty;

    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }

    [YamlMember(Alias = "relatedMemories")]
    public List<SkillRelatedMemoryFrontMatter>? RelatedMemories { get; set; }

    [YamlMember(Alias = "relatedSnippets")]
    public List<SkillRelatedSnippetFrontMatter>? RelatedSnippets { get; set; }

    [YamlMember(Alias = "source")]
    public SkillSourceFrontMatter? Source { get; set; }

    [YamlMember(Alias = "createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [YamlMember(Alias = "updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}
