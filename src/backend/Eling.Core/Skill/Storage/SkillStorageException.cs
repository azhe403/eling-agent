namespace Eling.Core.Skill.Storage;

/// <summary>Base type for every failure raised by skill storage. Never swallowed.</summary>
public class SkillStorageException : Exception
{
    public SkillStorageException(string message)
        : base(message)
    {
    }

    public SkillStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
