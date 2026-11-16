namespace Eling.Core.Skill;

/// <summary>
/// Pagination metadata for skill list results: rows in the catalog
/// (<see cref="Total"/>), rows matching the current filter
/// (<see cref="Matching"/>), and the requested page window.
/// </summary>
public readonly record struct SkillListMetadata(
    int Total,
    int Matching,
    int Limit,
    int Offset);
