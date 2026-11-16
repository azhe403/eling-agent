namespace Eling.Core.Skill;

/// <summary>
/// Ownership marker of a skill row. Lives only in the index and in memory —
/// never in frontmatter. <see cref="Eling"/> skills are created and managed
/// through Eling; <see cref="External"/> skills are owned by the tooling that
/// supplied them and are permanently read-only to every mutating path.
/// </summary>
public enum SkillOrigin
{
    Eling = 0,
    External = 1,
}

/// <summary>
/// The single public surface for the wire spelling of <see cref="SkillOrigin"/>
/// ("eling"/"external"). An unrecognised member throws rather than falling
/// back to its declared name, so a new origin has to be mapped here on purpose.
/// </summary>
public static class SkillOriginExtensions
{
    extension(SkillOrigin origin)
    {
        /// <summary>The canonical lowercase spelling used on the wire and in DTOs.</summary>
        public string WireName => origin switch
        {
            SkillOrigin.Eling => "eling",
            SkillOrigin.External => "external",
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown skill origin."),
        };
    }
}
