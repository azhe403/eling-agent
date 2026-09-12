namespace Eling.Core.Scope;

/// <summary>
/// One stored policy decision plus its two-level dates. <see cref="CreatedAt"/> is
/// the first time the decision was recorded and is immutable afterward;
/// <see cref="UpdatedAt"/> moves when the decision value changes. They are equal
/// at creation.
/// </summary>
public sealed record ProjectScopeEntry(
    ProjectScopeDecision Decision,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);
