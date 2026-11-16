using Eling.Core.Skill.Storage;

namespace Eling.Core.Skill;

/// <summary>
/// Create-or-update-by-name payload for <see cref="ISkillService.SaveAsync"/>.
/// The input is the complete new content of the skill; fields the input cannot
/// express (<see cref="Skill.Source"/>) are preserved from the existing file on
/// update. Sibling files are written verbatim into the folder.
/// </summary>
public sealed record SkillSaveInput(
    string Name,
    string Description,
    string Body,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyCollection<RelatedReference>? RelatedMemories = null,
    IReadOnlyCollection<RelatedReference>? RelatedSnippets = null,
    IReadOnlyList<SkillSiblingFile>? SiblingFiles = null);
