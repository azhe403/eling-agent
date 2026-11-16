namespace Eling.Core.Skill;

/// <summary>
/// A skill (or an active trash entry for one) was expected to exist but does
/// not — typically a name that is neither in the index nor in the active
/// catalog, or a trash restore/delete for a name that was never trashed.
/// </summary>
public sealed class SkillNotFoundException : Exception
{
    public SkillNotFoundException(string message)
        : base(message)
    {
    }

    public SkillNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
