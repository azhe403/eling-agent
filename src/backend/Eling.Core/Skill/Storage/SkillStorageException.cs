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

/// <summary>
/// A SKILL.md could not be parsed into an Eling-managed skill: invalid YAML,
/// missing/invalid keys, or frontmatter that violates the controlled
/// vocabularies (relation / scope). Vocabulary violations are rejected loudly
/// with the offending field and value rather than being dropped or coerced —
/// the file needs a human fix, and silent loss of cross-references is worse
/// than a clear error.
/// </summary>
public sealed class SkillFrontMatterException : SkillStorageException
{
    public SkillFrontMatterException(string message)
        : base(message)
    {
    }

    public SkillFrontMatterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
