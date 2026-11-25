namespace Eling.Core.Docs;

/// <summary>Append-only audit history. Never part of a disposable index.</summary>
public interface IDocsAuditStore
{
    Task WriteAsync(DocAuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<DocAuditEntry>> GetAsync(string? docId = null, int limit = 100, CancellationToken ct = default);
}
