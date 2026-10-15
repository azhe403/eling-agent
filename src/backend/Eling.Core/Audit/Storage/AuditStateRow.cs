namespace Eling.Core.Audit.Storage;

public sealed record AuditStateRow(
    long StateId,
    long EventId,
    string Side,
    AuditMetadata State);
