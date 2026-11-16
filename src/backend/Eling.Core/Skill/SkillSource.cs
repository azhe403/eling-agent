namespace Eling.Core.Skill;

/// <summary>
/// Optional upstream provenance of a skill ("where this came from"). Metadata
/// only — Eling never acts on it. Not serialized to frontmatter beyond the
/// canonical <c>source: { upstream, tracked }</c> shape.
/// </summary>
public sealed record SkillSource(
    string? Upstream,
    bool Tracked = false);
