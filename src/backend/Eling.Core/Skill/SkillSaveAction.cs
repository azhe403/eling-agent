namespace Eling.Core.Skill;

/// <summary>
/// The outcome signal a skill save reports to its caller. Surfaced on the
/// wire by the <c>skill_save</c> MCP tool as the <c>action</c> field of
/// <see cref="SkillSaveResult"/> / <c>SaveSkillResponse</c>, with the
/// lowercase values "created" and "updated" — identical spelling to
/// <c>memory_save</c>'s <c>action</c>.
/// </summary>
/// <remarks>
/// Deliberately a separate enum from <c>Eling.Core.Memory.SaveAction</c>:
/// the skill subsystem must not take a dependency on the memory subsystem.
/// </remarks>
public enum SkillSaveAction
{
    /// <summary>No index row existed for the name (and no unscanned folder either); the skill was created.</summary>
    Created,

    /// <summary>An index row existed for the name; the existing skill's content was replaced (ULID and createdAt preserved).</summary>
    Updated,
}
