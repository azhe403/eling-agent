namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using Xunit;

public class DocsAuditStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDocsAuditStore _store;

    public DocsAuditStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eling-docs-audit-{Guid.NewGuid():N}.db");
        _store = new SqliteDocsAuditStore(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task WriteAuditAsync_WithDocIdMissingFromDocs_DoesNotThrow()
    {
        await _store.WriteAsync(new DocAuditEntry(
            Ulid.NewUlid().ToString(), "not-indexed-doc", "mcp:doc_save", "create", null, null, DateTimeOffset.UtcNow));

        var audit = await _store.GetAsync("not-indexed-doc");
        Assert.Single(audit);
    }

    [Fact]
    public async Task DeleteAsync_KeepsAuditHistory()
    {
        await _store.WriteAsync(new DocAuditEntry(
            Ulid.NewUlid().ToString(), "doc-1", "mcp:doc_save", "create", null, null, DateTimeOffset.UtcNow));

        var audit = await _store.GetAsync("doc-1");
        Assert.Single(audit);
    }
}
