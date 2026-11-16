namespace Eling.Core.Skill;

/// <summary>
/// Input failed validation: a non-kebab-case name, a description over
/// <see cref="Skill.MaxDescriptionLength"/> characters, an empty body, or a
/// related reference outside the controlled vocabularies.
/// </summary>
public sealed class SkillValidationException : Exception
{
    public SkillValidationException(string message)
        : base(message)
    {
    }

    public SkillValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
