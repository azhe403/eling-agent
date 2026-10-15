namespace Eling.Core.Audit.Storage;

public sealed record AuditRecord(
    string File,
    int Line,
    AuditEvent Event);
