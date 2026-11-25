namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

public class DocsMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacyDb;
    private readonly string _auditDb;

    public DocsMigrationTests()
    {
        var key = Guid.NewGuid().ToString("N");
        _root = Path.Combine(Path.GetTempPath(), $"eling-migrate-{key}");
        Directory.CreateDirectory(_root);
        _legacyDb = Path.Combine(_root, "legacy.db");
        _auditDb = Path.Combine(_root, "audit.db");

        using var connection = new SqliteConnection($"Data Source={_legacyDb}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE docs (id TEXT PRIMARY KEY, rel_path TEXT, kind TEXT, topic TEXT, status INTEGER, read_order INTEGER, superseded_by TEXT);
            CREATE TABLE doc_audit (id TEXT PRIMARY KEY, doc_id TEXT, actor TEXT, action TEXT, before_json TEXT, after_json TEXT, at TEXT);
            INSERT INTO doc_audit VALUES ('1','doc-1','mcp:doc_save','create',NULL,NULL,'2026-11-25T00:00:00.0000000+00:00');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }

    [Fact]
    public async Task ExportLegacyAuditTwice_KeepsOneRow_AndDoesNotThrow()
    {
        var audit = new SqliteDocsAuditStore(_auditDb);
        await DocsMigration.ExportLegacyStateAsync(_legacyDb, audit);
        await DocsMigration.ExportLegacyStateAsync(_legacyDb, audit);

        Assert.Single(await audit.GetAsync());
    }
}
