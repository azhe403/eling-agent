namespace Eling.Core.Skill.Storage;

/// <summary>
/// A SKILL.md could not be parsed into an Eling-managed skill: invalid YAML,
/// missing/invalid keys, or frontmatter that violates the controlled
/// vocabularies (relation / scope). Vocabulary violations are rejected loudly
/// with the offending field and value rather than being dropped or coerced -
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
