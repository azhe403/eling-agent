namespace Eling.Core.Skill;

/// <summary>
/// One FTS hit against the skill index. <see cref="MatchedAt"/> carries the
/// index timestamp of the hit when the caller asked for recency information.
/// <see cref="Name"/> and <see cref="Description"/> are denormalized onto the
/// hit by the search query so callers that need only identity + summary
/// (e.g. recall) never re-hydrate the catalog.
/// </summary>
public readonly record struct SkillSearchResult(
    SkillId Id,
    double Rank,
    SkillOrigin Origin,
    string Path,
    DateTimeOffset? MatchedAt = null,
    string Name = "",
    string Description = "");
