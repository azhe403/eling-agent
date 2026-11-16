namespace Eling.Core.Skill.Serialization;

/// <summary>
/// The tolerant read of a SKILL.md document that is about to be adopted by
/// Eling (<see cref="SkillFileFormat.ParseForAdoption"/>). Carries exactly the
/// fields Eling preserves verbatim or re-validates on adoption; the
/// Eling-owned keys it rewrites (<c>id</c>, <c>createdAt</c>, <c>updatedAt</c>)
/// are deliberately absent.
/// </summary>
internal sealed record AdoptionRead(
    string Name,
    string Description,
    string Body,
    IReadOnlyCollection<string>? Tags,
    IReadOnlyList<RelatedReference> RelatedMemories,
    IReadOnlyList<RelatedReference> RelatedSnippets,
    SkillSource? Source);
