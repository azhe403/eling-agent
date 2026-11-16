namespace Eling.Core.Skill.Storage;

/// <summary>Shared validation used by both storage implementations. Name validation always happens before any IO.</summary>
internal static class SkillGuards
{
    public static void ValidateNameOrThrow(string? name, string paramName)
    {
        if (!Skill.ValidateName(name))
        {
            throw new ArgumentException(
                $"Skill name must be lowercase kebab-case (^[a-z0-9]+(-[a-z0-9]+)*$) and at most {Skill.MaxNameLength} characters.",
                paramName);
        }
    }
}
