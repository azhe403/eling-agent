namespace Eling.Core.Skill;

/// <summary>
/// The result of an <see cref="ISkillService.AdoptAsync"/> call: the identity
/// the skill carries after adoption (the index-assigned ULID is reused), the
/// folder it lives in, and where its SKILL.md is. The skill is a full
/// Eling-managed skill by then — the result is descriptive, not a handle.
/// </summary>
/// <param name="Id">The ULID; the index-assigned id when the catalog row had one.</param>
/// <param name="Name">The kebab-case name (doubles as the folder name).</param>
/// <param name="Path">Absolute path of the SKILL.md, as the storage layer sees it.</param>
/// <param name="Origin">Always <see cref="SkillOrigin.Eling"/> after a successful adoption.</param>
/// <param name="WasExternal">True when the skill was externally owned before the call.</param>
public sealed record AdoptResult(
    SkillId Id,
    string Name,
    string Path,
    SkillOrigin Origin,
    bool WasExternal);
