namespace Eling.Core.Audit.Storage;

public sealed record AuditQuery(
    AuditTrail Trail,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Action = null,
    string? Actor = null,
    int Limit = 200);
