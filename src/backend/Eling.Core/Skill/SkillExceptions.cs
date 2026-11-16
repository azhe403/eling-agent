namespace Eling.Core.Skill;

/// <summary>
/// A mutation targeted an externally-supplied skill. External skills are owned
/// by the tooling that created them; Eling reads and indexes them but never
/// writes, renames, trashes, or deletes them — this is a design decision, not
/// a v1 limitation. Callers should surface this as a clear "read-only" error.
/// </summary>
public sealed class SkillReadOnlyException : Exception
{
    public SkillReadOnlyException(string message)
        : base(message)
    {
    }

    public SkillReadOnlyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

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
