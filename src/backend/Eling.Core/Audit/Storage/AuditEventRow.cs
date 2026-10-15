namespace Eling.Core.Audit.Storage;

public sealed record AuditEventRow(
    long Id,
    string File,
    int Line,
    AuditEvent Event);
