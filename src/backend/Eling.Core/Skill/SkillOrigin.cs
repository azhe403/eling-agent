namespace Eling.Core.Skill;

/// <summary>
/// Ownership marker of a skill row. Lives only in the index and in memory -
/// never in frontmatter. <see cref="Eling"/> skills are created and managed
/// through Eling; <see cref="External"/> skills are owned by the tooling that
/// supplied them and are permanently read-only to every mutating path.
/// </summary>
public enum SkillOrigin
{
    Eling = 0,
    External = 1,
}
