namespace Eling.Core.Skill;

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
