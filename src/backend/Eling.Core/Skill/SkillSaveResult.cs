namespace Eling.Core.Skill;

/// <summary>
/// The outcome of a <see cref="ISkillService.SaveAsync"/> call: the saved
/// skill aggregate plus which side of the create/update boundary the call
/// landed on.
/// </summary>
/// <remarks>
/// The save input cannot express <see cref="Skill.Source"/>, so the existing
/// aggregate carries it across an update. <see cref="Action"/> is decided by
/// whether an index row existed for the name at save time: no row → Created,
/// an existing Eling-managed row (even a drifted one served from the index
/// mirror) → Updated.
/// </remarks>
public sealed record SkillSaveResult(
    Skill Skill,
    SkillSaveAction Action);
