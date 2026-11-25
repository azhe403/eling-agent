namespace Eling.Core.Docs.Storage;

using Microsoft.Data.Sqlite;

/// <summary>
/// Derived global index for docs: one database across all workspaces, tagged
/// by workspace_id. Audit and metadata live elsewhere; this database is disposable.
/// </summary>
public sealed class SqliteDocsIndex : IDocsIndex
{
    private const string CreateDocsSql = """
        CREATE TABLE IF NOT EXISTS docs (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            path TEXT NOT NULL,
            kind TEXT NOT NULL,
            title TEXT NOT NULL,
            status INTEGER NOT NULL,
            topic TEXT NOT NULL,
            read_order INTEGER NOT NULL DEFAULT 0,
            archived INTEGER NOT NULL DEFAULT 0,
            superseded_by TEXT NULL,
            steps_total INTEGER NOT NULL DEFAULT 0,
            steps_done INTEGER NOT NULL DEFAULT 0,
            file_hash TEXT NOT NULL,
            mtime TEXT NOT NULL,
            indexed_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS idx_docs_workspace ON docs(workspace_id);
        """;

    private const string CreateSectionsSql = """
        CREATE TABLE IF NOT EXISTS doc_sections (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            doc_id TEXT NOT NULL REFERENCES docs(id) ON DELETE CASCADE,
            anchor TEXT NOT NULL,
            heading TEXT NOT NULL,
            level INTEGER NOT NULL,
            line_start INTEGER NOT NULL,
            line_end INTEGER NOT NULL,
            status INTEGER NOT NULL,
            annotation TEXT NULL,
            steps_total INTEGER NOT NULL DEFAULT 0,
            steps_done INTEGER NOT NULL DEFAULT 0,
            UNIQUE(doc_id, anchor));
        """;

    private const string CreateRefsSql = """
        CREATE TABLE IF NOT EXISTS doc_refs (
            doc_id TEXT NOT NULL REFERENCES docs(id) ON DELETE CASCADE,
            anchor TEXT NOT NULL,
            raw_text TEXT NOT NULL,
            ref_kind INTEGER NOT NULL DEFAULT 0,
            resolved_path TEXT NULL,
            resolved_line INTEGER NULL,
            resolved INTEGER NOT NULL DEFAULT 0,
            resolved_at TEXT NULL,
            PRIMARY KEY (doc_id, anchor, raw_text, ref_kind));
        """;

    private const string CreateRelationsSql = """
        CREATE TABLE IF NOT EXISTS doc_relations (
            source_id TEXT NOT NULL REFERENCES docs(id) ON DELETE CASCADE,
            target_id TEXT NOT NULL REFERENCES docs(id) ON DELETE CASCADE,
            kind INTEGER NOT NULL,
            PRIMARY KEY (source_id, target_id, kind));
        """;

    private const string CreatePolicySql = """
        CREATE TABLE IF NOT EXISTS doc_policy (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _schemaReady;

    public SqliteDocsIndex(string databasePath)
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
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync();
        }
        await _initGate.WaitAsync();
        try
        {
            if (!_schemaReady)
            {
                await EnsureSchemaAsync(connection);
                _schemaReady = true;
            }
        }
        finally { _initGate.Release(); }
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('docs') WHERE name = 'workspace_id';";
        var current = Convert.ToInt32(await check.ExecuteScalarAsync() ?? 0L);
        if (current == 0)
        {
            // Preserve the legacy database before rebuilding the disposable
            // index — it holds the only copy of pre-unification audit history.
            var legacyPath = connection.DataSource;
            if (File.Exists(legacyPath))
            {
                var backup = $"{legacyPath}.legacy-{Ulid.NewUlid().ToString().ToLowerInvariant()}";
                File.Copy(legacyPath, backup, overwrite: false);
            }
            foreach (var sql in new[]
                     {
                         "DROP TABLE IF EXISTS doc_relations;", "DROP TABLE IF EXISTS doc_refs;",
                         "DROP TABLE IF EXISTS doc_sections;", "DROP TABLE IF EXISTS docs;",
                         "DROP TABLE IF EXISTS doc_audit;", "DROP TABLE IF EXISTS docs_fts_porter;",
                         "DROP TABLE IF EXISTS docs_fts_trigram;"
                     })
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = sql;
                await drop.ExecuteNonQueryAsync();
            }
        }

        foreach (var sql in new[]
                 {
                     CreateDocsSql, CreateSectionsSql, CreateRefsSql, CreateRelationsSql, CreatePolicySql,
                     "CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts_porter USING fts5(title, heading, body, tokenize='porter unicode61 remove_diacritics 1');",
                     "CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts_trigram USING fts5(title, heading, body, tokenize='trigram');"
                 })
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task IndexAsync(Doc doc, IReadOnlyList<DocSection> sections, IReadOnlyList<DocRef> refs, string? body = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        await using var connection = await OpenConnectionAsync();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO docs (id, workspace_id, path, kind, title, status, topic, read_order, archived,
                                  superseded_by, steps_total, steps_done, file_hash, mtime, indexed_at)
                VALUES ($id, $ws, $path, $kind, $title, $status, $topic, $order, $archived,
                        $super, $total, $done, $hash, $mtime, $indexed)
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id=excluded.workspace_id, path=excluded.path, kind=excluded.kind,
                    title=excluded.title, status=excluded.status, topic=excluded.topic,
                    read_order=excluded.read_order, archived=excluded.archived,
                    superseded_by=excluded.superseded_by, steps_total=excluded.steps_total,
                    steps_done=excluded.steps_done, file_hash=excluded.file_hash,
                    mtime=excluded.mtime, indexed_at=excluded.indexed_at;
                """;
            Add(upsert, "$id", doc.Id.Value);
            Add(upsert, "$ws", doc.WorkspaceId);
            Add(upsert, "$path", doc.Path);
            Add(upsert, "$kind", doc.Kind.ToString().ToLowerInvariant());
            Add(upsert, "$title", doc.Title);
            Add(upsert, "$status", doc.Status.Value);
            Add(upsert, "$topic", doc.Topic);
            Add(upsert, "$order", doc.Order);
            Add(upsert, "$archived", doc.Archived ? 1 : 0);
            Add(upsert, "$super", (object?)doc.SupersededBy ?? DBNull.Value);
            Add(upsert, "$total", doc.StepsTotal);
            Add(upsert, "$done", doc.StepsDone);
            Add(upsert, "$hash", doc.FileHash);
            Add(upsert, "$mtime", doc.MTime.ToString("O"));
            Add(upsert, "$indexed", doc.IndexedAt.ToString("O"));
            await upsert.ExecuteNonQueryAsync();
        }

        await ClearFtsAsync(connection, tx, doc.Id.Value);
        foreach (var section in sections)
        {
            long sectionId;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO doc_sections (doc_id, anchor, heading, level, line_start, line_end, status, annotation, steps_total, steps_done)
                    VALUES ($id, $anchor, $heading, $level, $start, $end, $status, $annotation, $total, $done)
                    RETURNING id;
                    """;
                Add(insert, "$id", doc.Id.Value);
                Add(insert, "$anchor", section.Anchor);
                Add(insert, "$heading", section.Heading);
                Add(insert, "$level", section.Level);
                Add(insert, "$start", section.LineStart);
                Add(insert, "$end", section.LineEnd);
                Add(insert, "$status", section.Status.Value);
                Add(insert, "$annotation", (object?)section.Annotation ?? DBNull.Value);
                Add(insert, "$total", section.StepsTotal);
                Add(insert, "$done", section.StepsDone);
                sectionId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            }
            foreach (var table in new[] { "docs_fts_porter", "docs_fts_trigram" })
            {
                await using var fts = connection.CreateCommand();
                fts.Transaction = tx;
                fts.CommandText = $"INSERT INTO {table}(rowid, title, heading, body) VALUES ($rowid, $title, $heading, $body);";
                Add(fts, "$rowid", sectionId);
                Add(fts, "$title", doc.Title);
                Add(fts, "$heading", section.Heading);
                Add(fts, "$body", string.IsNullOrEmpty(body) ? section.Heading : body);
                await fts.ExecuteNonQueryAsync();
            }
        }

        foreach (var reference in refs)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR REPLACE INTO doc_refs (doc_id, anchor, raw_text, ref_kind, resolved_path, resolved_line, resolved, resolved_at)
                VALUES ($id, $anchor, $raw, $kind, $path, $line, $resolved, $at);
                """;
            Add(insert, "$id", doc.Id.Value);
            Add(insert, "$anchor", reference.Anchor);
            Add(insert, "$raw", reference.RawText);
            Add(insert, "$kind", (int)reference.RefKind);
            Add(insert, "$path", (object?)reference.ResolvedPath ?? DBNull.Value);
            Add(insert, "$line", (object?)reference.ResolvedLine ?? DBNull.Value);
            Add(insert, "$resolved", reference.Resolved ? 1 : 0);
            Add(insert, "$at", (object?)reference.ResolvedAt?.ToString("O") ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private static async Task ClearFtsAsync(SqliteConnection connection, SqliteTransaction tx, string docId)
    {
        await using var rows = connection.CreateCommand();
        rows.Transaction = tx;
        rows.CommandText = "SELECT id FROM doc_sections WHERE doc_id = $id;";
        Add(rows, "$id", docId);
        var ids = new List<long>();
        await using (var reader = await rows.ExecuteReaderAsync())
            while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        await using var delSections = connection.CreateCommand();
        delSections.Transaction = tx;
        delSections.CommandText = "DELETE FROM doc_sections WHERE doc_id = $id;";
        Add(delSections, "$id", docId);
        await delSections.ExecuteNonQueryAsync();
        await using var delRefs = connection.CreateCommand();
        delRefs.Transaction = tx;
        delRefs.CommandText = "DELETE FROM doc_refs WHERE doc_id = $id;";
        Add(delRefs, "$id", docId);
        await delRefs.ExecuteNonQueryAsync();
        foreach (var id in ids)
        foreach (var table in new[] { "docs_fts_porter", "docs_fts_trigram" })
        {
            await using var delFts = connection.CreateCommand();
            delFts.Transaction = tx;
            delFts.CommandText = $"DELETE FROM {table} WHERE rowid = $rowid;";
            Add(delFts, "$rowid", id);
            await delFts.ExecuteNonQueryAsync();
        }
    }

    public async Task DeleteAsync(string docId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();
        await ClearFtsAsync(connection, tx, docId);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM docs WHERE id = $id;";
        Add(cmd, "$id", docId);
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task RebuildAsync(IEnumerable<DocRebuildItem> items)
    {
        await using var connection = await OpenConnectionAsync();
        await using var clear = connection.CreateCommand();
        clear.CommandText = "DELETE FROM docs;";
        await clear.ExecuteNonQueryAsync();
        foreach (var item in items) await IndexAsync(item.Doc, item.Sections, item.Refs);
    }

    public async Task<Doc?> GetAsync(string docId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM docs WHERE id = $id;";
        Add(cmd, "$id", docId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadDoc(reader) : null;
    }

    public async Task<Doc?> GetByPathAsync(string workspaceId, string path, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM docs WHERE workspace_id = $ws AND path = $path LIMIT 1;";
        Add(cmd, "$ws", workspaceId);
        Add(cmd, "$path", path);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadDoc(reader) : null;
    }

    public async Task<CoveragePolicy> GetPolicyAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM doc_policy WHERE key = 'coverage';";
        var value = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(value)) return new CoveragePolicy();
        return System.Text.Json.JsonSerializer.Deserialize<CoveragePolicy>(value) ?? new CoveragePolicy();
    }

    public async Task SetPolicyAsync(CoveragePolicy policy, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO doc_policy(key, value) VALUES ('coverage', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        Add(cmd, "$value", System.Text.Json.JsonSerializer.Serialize(policy));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DocSection?> GetSectionAsync(string docId, string anchor)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT doc_id, anchor, heading, level, line_start, line_end, status, annotation, steps_total, steps_done FROM doc_sections WHERE doc_id=$id AND anchor=$anchor;";
        Add(cmd, "$id", docId);
        Add(cmd, "$anchor", anchor);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSection(reader) : null;
    }

    public async Task<IReadOnlyList<DocSection>> GetSectionsAsync(string docId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT doc_id, anchor, heading, level, line_start, line_end, status, annotation, steps_total, steps_done FROM doc_sections WHERE doc_id=$id ORDER BY line_start;";
        Add(cmd, "$id", docId);
        var result = new List<DocSection>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadSection(reader));
        return result;
    }

    public async Task<IReadOnlyList<DocRef>> GetRefsAsync(string docId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT doc_id, anchor, raw_text, ref_kind, resolved_path, resolved_line, resolved, resolved_at FROM doc_refs WHERE doc_id=$id;";
        Add(cmd, "$id", docId);
        var result = new List<DocRef>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var reference = new DocRef(
                DocId.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                (ReferenceKind)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6) == 1)
            { ResolvedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)) };
            result.Add(reference);
        }
        return result;
    }

    public async Task<IReadOnlyList<DocRelation>> GetRelationsAsync(string docId, bool asSource)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = asSource
            ? "SELECT source_id, target_id, kind FROM doc_relations WHERE source_id=$id;"
            : "SELECT source_id, target_id, kind FROM doc_relations WHERE target_id=$id;";
        Add(cmd, "$id", docId);
        var result = new List<DocRelation>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new DocRelation(DocId.Parse(reader.GetString(0)), DocId.Parse(reader.GetString(1)), (RelationKind)reader.GetInt32(2)));
        return result;
    }

    public async Task UpsertRelationAsync(DocRelation relation)
    {
        await using var connection = await OpenConnectionAsync();
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM docs WHERE id IN ($source, $target);";
        Add(exists, "$source", relation.SourceId.Value);
        Add(exists, "$target", relation.TargetId.Value);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync() ?? 0L) != 2) return;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO doc_relations (source_id, target_id, kind) VALUES ($source, $target, $kind);";
        Add(cmd, "$source", relation.SourceId.Value);
        Add(cmd, "$target", relation.TargetId.Value);
        Add(cmd, "$kind", (int)relation.Kind);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteRelationAsync(string sourceId, string targetId, RelationKind kind)
    {
        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM doc_relations WHERE source_id=$source AND target_id=$target AND kind=$kind;";
        Add(cmd, "$source", sourceId);
        Add(cmd, "$target", targetId);
        Add(cmd, "$kind", (int)kind);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<DocSearchResult>> SearchAsync(
        string query, string? workspaceId = null, DocKind? kind = null, string? topic = null,
        string? pathPrefix = null, int limit = 10)
    {
        var fts = "docs_fts_porter";
        var where = new List<string> { $"{fts} MATCH $query" };
        if (workspaceId is not null) where.Add("d.workspace_id = $ws");
        if (kind.HasValue) where.Add("d.kind = $kind");
        if (!string.IsNullOrWhiteSpace(topic)) where.Add("d.topic = $topic");
        if (!string.IsNullOrWhiteSpace(pathPrefix)) where.Add("d.path LIKE $prefix");

        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT d.id, d.workspace_id, d.path, d.kind, d.title, d.status, d.topic,
                   d.steps_total, d.steps_done, bm25({fts}) AS rank
            FROM {fts}
            JOIN doc_sections s ON s.rowid = {fts}.rowid
            JOIN docs d ON d.id = s.doc_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY rank
            LIMIT $limit;
            """;
        Add(cmd, "$query", query);
        if (workspaceId is not null) Add(cmd, "$ws", workspaceId);
        if (kind.HasValue) Add(cmd, "$kind", kind.Value.ToString().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(topic)) Add(cmd, "$topic", topic);
        if (!string.IsNullOrWhiteSpace(pathPrefix)) Add(cmd, "$prefix", pathPrefix + "%");
        Add(cmd, "$limit", limit);

        var results = new List<DocSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new DocSearchResult(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Enum.Parse<DocKind>(reader.GetString(3), true), reader.GetString(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), -reader.GetDouble(9)));
        return results;
    }

    public async Task<IReadOnlyList<DocListItem>> ListAsync(
        string? workspaceId = null, DocKind? kind = null, string? topic = null,
        string sort = "name", string order = "asc", bool includeArchived = false)
    {
        var key = sort?.ToLowerInvariant() switch
        {
            "path" => "path", "kind" => "kind", "status" => "status",
            "order" => "read_order", "updated" => "mtime", _ => "title"
        };
        var direction = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var where = new List<string>();
        if (workspaceId is not null) where.Add("workspace_id = $ws");
        if (kind.HasValue) where.Add("kind = $kind");
        if (!string.IsNullOrWhiteSpace(topic)) where.Add("topic = $topic");
        if (!includeArchived) where.Add("archived = 0");
        var clause = " WHERE " + string.Join(" AND ", where);

        await using var connection = await OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, workspace_id, path, kind, title, status, topic, read_order, archived, steps_total, steps_done
            FROM docs{clause}
            ORDER BY CASE WHEN {key} IS NULL THEN 1 ELSE 0 END, {key} {direction}, title ASC;
            """;
        if (workspaceId is not null) Add(cmd, "$ws", workspaceId);
        if (kind.HasValue) Add(cmd, "$kind", kind.Value.ToString().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(topic)) Add(cmd, "$topic", topic);

        var results = new List<DocListItem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new DocListItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Enum.Parse<DocKind>(reader.GetString(3), true), reader.GetString(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8) == 1,
                reader.GetInt32(9), reader.GetInt32(10)));
        return results;
    }

    private static Doc ReadDoc(SqliteDataReader reader) => new(
        DocId.Parse(reader.GetString(reader.GetOrdinal("id"))),
        reader.GetString(reader.GetOrdinal("workspace_id")),
        reader.GetString(reader.GetOrdinal("path")),
        Enum.Parse<DocKind>(reader.GetString(reader.GetOrdinal("kind")), true),
        reader.GetString(reader.GetOrdinal("title")),
        new DocStatusValue((DocKind)0, reader.GetInt32(reader.GetOrdinal("status"))),
        reader.GetString(reader.GetOrdinal("topic")),
        reader.GetInt32(reader.GetOrdinal("read_order")),
        reader.GetInt32(reader.GetOrdinal("archived")) == 1,
        reader.IsDBNull(reader.GetOrdinal("superseded_by")) ? null : reader.GetString(reader.GetOrdinal("superseded_by")),
        reader.GetInt32(reader.GetOrdinal("steps_total")),
        reader.GetInt32(reader.GetOrdinal("steps_done")),
        reader.GetString(reader.GetOrdinal("file_hash")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("mtime"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("indexed_at"))));

    private static DocSection ReadSection(SqliteDataReader reader) => new(
        DocId.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
        reader.GetInt32(4), reader.GetInt32(5), new DocStatusValue((DocKind)0, reader.GetInt32(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8), reader.GetInt32(9));

    private static void Add(SqliteCommand command, string name, object value)
        => command.Parameters.AddWithValue(name, value);
}
