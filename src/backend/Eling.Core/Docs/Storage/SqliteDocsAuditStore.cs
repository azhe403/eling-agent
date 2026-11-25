namespace Eling.Core.Docs.Storage;

using Microsoft.Data.Sqlite;

/// <summary>
/// Persistent audit store. doc_id deliberately has no FK to docs(id): audit
/// must survive deletion of the document and belongs to no origin index.
/// </summary>
public sealed class SqliteDocsAuditStore : IDocsAuditStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _schemaReady;

    public SqliteDocsAuditStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync();
        }
        await _initGate.WaitAsync();
        try
        {
            if (!_schemaReady)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS doc_audit (
                        id TEXT PRIMARY KEY,
                        doc_id TEXT NOT NULL,
                        actor TEXT NOT NULL,
                        action TEXT NOT NULL,
                        before_json TEXT NULL,
                        after_json TEXT NULL,
                        at TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS idx_doc_audit_doc ON doc_audit(doc_id);
                    CREATE INDEX IF NOT EXISTS idx_doc_audit_at ON doc_audit(at);
                    """;
                await cmd.ExecuteNonQueryAsync();
                _schemaReady = true;
            }
        }
        finally { _initGate.Release(); }
        return connection;
    }

    public async Task WriteAsync(DocAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO doc_audit (id, doc_id, actor, action, before_json, after_json, at)
            VALUES ($id, $docId, $actor, $action, $before, $after, $at);
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$docId", entry.DocId);
        cmd.Parameters.AddWithValue("$actor", entry.Actor);
        cmd.Parameters.AddWithValue("$action", entry.Action);
        cmd.Parameters.AddWithValue("$before", (object?)entry.BeforeJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$after", (object?)entry.AfterJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", entry.At.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DocAuditEntry>> GetAsync(string? docId = null, int limit = 100, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = docId is null
            ? "SELECT id, doc_id, actor, action, before_json, after_json, at FROM doc_audit ORDER BY at DESC LIMIT $limit;"
            : "SELECT id, doc_id, actor, action, before_json, after_json, at FROM doc_audit WHERE doc_id = $docId ORDER BY at DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        if (docId is not null) cmd.Parameters.AddWithValue("$docId", docId);
        var entries = new List<DocAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new DocAuditEntry(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return entries;
    }
}
