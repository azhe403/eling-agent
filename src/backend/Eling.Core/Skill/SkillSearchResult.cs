namespace Eling.Core.Skill;

/// <summary>
/// One FTS hit against the skill index. <see cref="MatchedAt"/> carries the
/// index timestamp of the hit when the caller asked for recency information.
/// </summary>
public readonly record struct SkillSearchResult(
    SkillId Id,
    double Rank,
    SkillOrigin Origin,
    string Path,
    DateTimeOffset? MatchedAt = null);
