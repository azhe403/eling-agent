namespace Eling.Core.Skill;

/// <summary>
/// A cross-reference from one entry to another, matching the frontmatter
/// <c>relatedMemories</c> / <c>relatedSnippets</c> shapes. <see cref="Scope"/>
/// is the referenced memory's scope label (<c>global</c>, <c>project</c>,
/// <c>project-local</c>); snippet references omit it. <see cref="Relation"/>
/// uses the shared relation vocabulary (<c>implements</c>, <c>anti-pattern</c>,
/// <c>example</c>, <c>test-case</c>, <c>reference</c>).
/// </summary>
public sealed record RelatedReference
{
    public RelatedReference(string id, string? scope, string relation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        Id = id;
        Scope = scope;
        Relation = relation;
    }

    public string Id { get; }

    public string? Scope { get; }

    public string Relation { get; }
}
