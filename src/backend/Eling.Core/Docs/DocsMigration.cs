namespace Eling.Core.Docs;

using Microsoft.Data.Sqlite;

/// <summary>
/// One-time cutover: lifts non-inferable state out of the legacy SQLite
/// indexes into the committed JSON and the persistent audit store. The old
/// indexes are disposable caches; the Markdown files were already moved.
/// </summary>
public static class DocsMigration
{
    public static async Task ExportLegacyStateAsync(
        string legacyIndexPath,
        DocsMetadataStore metadata,
        IDocsAuditStore audit,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        if (!File.Exists(legacyIndexPath)) return;

        var workspaceId = DocsMetadataStore.ResolveWorkspaceId(workspaceRoot);
        var (document, _) = await metadata.LoadWithFindingsAsync(workspaceRoot, ct);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = legacyIndexPath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var docs = connection.CreateCommand())
        {
            docs.CommandText = "SELECT id, rel_path, kind, topic, status, read_order, superseded_by FROM docs;";
            await using var reader = await docs.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var path = reader.GetString(1);
                if (document.Docs.ContainsKey(path)) continue;
                var entry = new DocsMetadataEntry
                {
                    Id = DocId.NewId().Value,
                    Path = path,
                    WorkspaceId = workspaceId,
                    Kind = Enum.TryParse<DocKind>(reader.GetString(2), true, out var kind) ? kind : DocKind.Doc,
                    Topic = reader.GetString(3),
                    Status = reader.GetInt32(4),
                    Order = reader.GetInt32(5),
                    SupersededBy = reader.IsDBNull(6) ? null : reader.GetString(6)
                };
                document.Docs[path] = entry;
            }
        }

        await using (var audits = connection.CreateCommand())
        {
            audits.CommandText = "SELECT id, doc_id, actor, action, before_json, after_json, at FROM doc_audit;";
            await using var reader = await audits.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                await audit.WriteAsync(new DocAuditEntry(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    DateTimeOffset.Parse(reader.GetString(6))), ct);
            }
        }

        await metadata.SaveAsync(workspaceRoot, document, ct);
    }
}
