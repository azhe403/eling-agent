namespace Eling.Core.Audit;

public sealed class NullAuditLogger : IAuditLogger
{
    public static readonly NullAuditLogger Instance = new();

    private NullAuditLogger()
    {
    }

    public ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
