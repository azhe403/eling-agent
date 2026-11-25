namespace Eling.Core.Docs;

using Microsoft.Data.Sqlite;

/// <summary>
/// One-time cutover: lift audit rows out of a legacy SQLite index before the
/// disposable index is rebuilt. Metadata now lives in the new index itself.
/// </summary>
public static class DocsMigration
{
    public static async Task ExportLegacyStateAsync(
        string legacyIndexPath,
        IDocsAuditStore audit,
        CancellationToken ct = default)
    {
        if (!File.Exists(legacyIndexPath)) return;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = legacyIndexPath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, doc_id, actor, action, before_json, after_json, at FROM doc_audit;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            if (await audit.GetAsync(id, 1, ct) is { Count: > 0 }) continue;
            await audit.WriteAsync(new DocAuditEntry(
                id, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6))), ct);
        }
    }
}
