namespace Eling.Core.Skill.Storage;

/// <summary>
/// Canonical file read/write/delete/restore/rename/list for the skill
/// catalog (<c>~/.agents/skills/{name}/</c>) and the catalog-local trash
/// (<c>~/.agents/.trash/skills/{name}/</c>). Implementations never write
/// outside those roots.
/// </summary>
public interface ISkillStorage
{
    /// <summary>
    /// Writes the skill as <c>skills/{name}/SKILL.md</c>. Creates or replaces
    /// the skill folder (overwriting an existing active skill keeps its ULID
    /// when the caller passes the same identity back in). When
    /// <paramref name="siblingFiles"/> is supplied, those files are written
    /// verbatim into the folder; paths are validated before any IO. A failure
    /// leaves no partial skill behind. Refuses when the name is occupied by a
    /// trashed skill, and never writes an externally-origin skill.
    /// </summary>
    Task SaveAsync(Skill skill, IReadOnlyList<SkillSiblingFile>? siblingFiles = null);

    /// <summary>Returns the skill stored under <paramref name="name"/>, or <c>null</c> when the folder/SKILL.md is missing. Throws <see cref="SkillFrontMatterException"/> when the file cannot be parsed.</summary>
    Task<Skill?> GetByNameAsync(string name);

    /// <summary>
    /// Lists every readable Eling-managed skill in the active catalog,
    /// ordered by name (ordinal). Malformed folders (invalid name, missing
    /// SKILL.md, unparseable frontmatter) are skipped with a logged warning —
    /// never thrown.
    /// </summary>
    Task<IReadOnlyList<Skill>> ListAllAsync();

    /// <summary>
    /// Moves the skill folder <c>{oldName}</c> → <c>{newName}</c> and rewrites
    /// the <c>name</c> key inside SKILL.md atomically; the ULID (and body,
    /// tags, references, timestamps) is preserved. Throws
    /// <see cref="SkillConflictException"/> when the destination name is
    /// occupied (active or trashed).
    /// </summary>
    Task RenameAsync(string oldName, string newName);

    /// <summary>Moves the skill folder into the catalog-local trash. Throws <see cref="SkillConflictException"/> when the trash destination is occupied — destinations are never merged.</summary>
    Task MoveToTrashAsync(string name);

    /// <summary>Moves the skill folder out of the trash back into the active catalog. Throws <see cref="SkillConflictException"/> when the active destination is occupied.</summary>
    Task RestoreAsync(string name);

    /// <summary>Permanently deletes the skill folder. Operates on the trash only — an active (non-trashed) skill is not deleted.</summary>
    Task DeleteAsync(string name);

    /// <summary>True when an active skill folder with a SKILL.md exists under <paramref name="name"/>.</summary>
    Task<bool> ExistsAsync(string name);
}
