namespace Eling.Core.Audit.Storage;

public interface IAuditIndex
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task IndexAsync(IReadOnlyList<AuditRecord> records, CancellationToken cancellationToken);
    Task RebuildAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken);
}
