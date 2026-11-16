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
