namespace Eling.Core.Audit;

public interface IAuditLogger
{
    ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default);
}
