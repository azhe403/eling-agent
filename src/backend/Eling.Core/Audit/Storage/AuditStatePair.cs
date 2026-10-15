namespace Eling.Core.Audit.Storage;

public sealed record AuditStatePair(
    AuditMetadata? After,
    AuditMetadata? Previous);
