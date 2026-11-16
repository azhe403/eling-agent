namespace Eling.Core.Skill.Storage;

/// <summary>
/// A name or destination collides with an existing skill (active or trashed).
/// Trash destinations are never merged: a conflict must be resolved by the
/// caller (restore / permanent delete), not by storage.
/// </summary>
public sealed class SkillConflictException : SkillStorageException
{
    public SkillConflictException(string message)
        : base(message)
    {
    }

    public SkillConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
