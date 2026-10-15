namespace Eling.Core.Audit.Retention;

/// <summary>
/// One day's aggregate for one (category, action, actor, outcome) group.
/// </summary>
/// <remarks>
/// <see cref="Category"/> and <see cref="Outcome"/> are typed rather than
/// strings (amendment C1): the same kebab-case converters that serialize the
/// JSONL entries then apply here, so a rollup — retained indefinitely and thus
/// a permanent artifact — carries the identical labels the entries do, instead
/// of the PascalCase enum names ("Memory"/"Success") this record used to write.
/// </remarks>
public sealed record AuditRollup(
    DateTimeOffset Date,
    AuditTrail Trail,
    AuditCategory Category,
    string Action,
    string Actor,
    AuditOutcome Outcome,
    int Count,
    int? MinMs,
    int? MedianMs,
    int? MaxMs);
