namespace Eling.Core.Skill;

/// <summary>
/// The controlled vocabularies used in skill frontmatter cross-references
/// (<c>relatedMemories</c> / <c>relatedSnippets</c>). Kept public so the MCP
/// layer can validate at write time too. Anything outside these sets is
/// rejected with an explicit <see cref="Storage.SkillFrontMatterException"/>
/// at the file boundary — never silently accepted, never dropped.
/// </summary>
public static class SkillVocabulary
{
    /// <summary>The shared relation vocabulary (same set snippets use).</summary>
    public static readonly IReadOnlyList<string> Relations =
        ["implements", "anti-pattern", "example", "test-case", "reference"];

    /// <summary>The scope labels a related <em>memory</em> reference may carry.</summary>
    public static readonly IReadOnlyList<string> MemoryScopes =
        ["global", "project", "project-local"];

    private static readonly HashSet<string> RelationSet = [.. Relations];
    private static readonly HashSet<string> MemoryScopeSet = [.. MemoryScopes];

    public static bool IsRelation(string? value) =>
        value is not null && RelationSet.Contains(value);

    public static bool IsMemoryScope(string? value) =>
        value is not null && MemoryScopeSet.Contains(value);
}
