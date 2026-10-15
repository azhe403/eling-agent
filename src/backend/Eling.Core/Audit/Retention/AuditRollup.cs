namespace Eling.Core.Audit.Retention;

public sealed record AuditRollup(
    DateTimeOffset Date,
    AuditTrail Trail,
    string Category,
    string Action,
    string Actor,
    string Outcome,
    int Count,
    int? MinMs,
    int? MedianMs,
    int? MaxMs);
