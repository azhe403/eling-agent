namespace Eling.Core.Docs.Storage;

using System.Data.Common;
using Microsoft.Data.Sqlite;

/// <summary>
/// Dedicated <c>docs-index.db</c> FTS5 index for the docs catalog.
/// Dual FTS5: porter unicode61 (tier-1) + trigram (tier-2).
/// Schema matches spec §3.3.
/// </summary>
public sealed class SqliteDocsIndex : IDocsIndex
{
    // Schema per spec §3.3
    private const string CreateDocsSql = """
        CREATE TABLE IF NOT EXISTS docs (
            id TEXT PRIMARY KEY,              -- "<ownership>:<origin>:<sha1(rel_path)>"
            ownership TEXT NOT NULL,          -- managed | external
            origin TEXT NOT NULL,             -- current | ancestor | shard | global
            rel_path TEXT NOT NULL,
            kind TEXT NOT NULL,               -- spec | plan | doc | scratch
            title TEXT NOT NULL,
            status INTEGER NOT NULL,          -- per-kind enum value
            topic TEXT NOT NULL,
            read_order INTEGER NOT NULL DEFAULT 0,
            superseded_by TEXT NULL,
            steps_total INTEGER NOT NULL DEFAULT 0,
            steps_done INTEGER NOT NULL DEFAULT 0,
            file_hash TEXT NOT NULL,
            mtime TEXT NOT NULL,
            indexed_at TEXT NOT NULL,
            frontmatter_error INTEGER NOT NULL DEFAULT 0,
            frontmatter_error_msg TEXT NULL);
        """;

    private const string CreateSectionsSql = """
        CREATE TABLE IF NOT EXISTS doc_sections (
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
            PRIMARY KEY (doc_id, anchor));
        """;

    private const string CreateRefsSql = """
        CREATE TABLE IF NOT EXISTS doc_refs (
            doc_id TEXT NOT NULL REFERENCES docs(id) ON DELETE CASCADE,
            anchor TEXT NOT NULL,
            raw_text TEXT NOT NULL,
            ref_kind INTEGER NOT NULL DEFAULT 0,  -- 0=Symbol, 1=Path, 2=AnchorLink
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
            kind INTEGER NOT NULL,  -- 0=Implements, 1=Supersedes, 2=References
            PRIMARY KEY (source_id, target_id, kind));
        """;

    private const string CreateAuditSql = """
        CREATE TABLE IF NOT EXISTS doc_audit (
            id TEXT PRIMARY KEY,                  -- ULID
            doc_id TEXT NOT NULL REFERENCES docs(id) ON DELETE CASCADE,
            actor TEXT NOT NULL,                  -- "mcp:doc_save", "dashboard", "desktop:<id>"
            action TEXT NOT NULL,                 -- create | update | delete | annotate | promote | relate
            before_json TEXT NULL,                -- snapshot before
            after_json TEXT NULL,                 -- snapshot after
            at TEXT NOT NULL);                    -- ISO 8601
        CREATE INDEX IF NOT EXISTS idx_doc_audit_doc ON doc_audit(doc_id);
        CREATE INDEX IF NOT EXISTS idx_doc_audit_at ON doc_audit(at);
        """;

    private const string CreatePolicySql = """
        CREATE TABLE IF NOT EXISTS doc_policy (
            key TEXT PRIMARY KEY,                 -- "coverage" | "decision" | "offer"
            value TEXT NOT NULL,                  -- JSON
            updated_at TEXT NOT NULL);
        """;

    private const string CreateOfferSql = """
        CREATE TABLE IF NOT EXISTS doc_offer (
            project_hash TEXT PRIMARY KEY,        -- SHA1 of canonical root
            state TEXT NOT NULL,                  -- unasked | accepted | declined | never
            asked_at TEXT NULL,
            answered_at TEXT NULL,
            specs_since_ask INTEGER NOT NULL DEFAULT 0);
        """;

    // Dual FTS5: porter unicode61 (tier-1) + trigram (tier-2)
    // Using contentless FTS5 (no content=) since doc_sections doesn't store body content
    private const string CreateFtsPorterSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts_porter USING fts5(
            title, heading, body,
            tokenize='porter unicode61 remove_diacritics 1');
        """;

    private const string CreateFtsTrigramSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts_trigram USING fts5(
            title, heading, body,
            tokenize='trigram');
        """;

    private const int AndToOrThreshold = 3;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _schemaReady;

    public SqliteDocsIndex(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
    }

    public async Task IndexAsync(Doc doc, IReadOnlyList<DocSection> sections, IReadOnlyList<DocRef> refs)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(sections);

        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await UpsertDocAsync(connection, transaction, doc);

        // Delete existing sections/refs for this doc (for FTS sync) - does NOT delete the doc
        await DeleteSectionsAndRefsOnlyAsync(connection, transaction, doc.Id.Value);

        // Insert sections with FTS sync
        foreach (var section in sections)
        {
            await InsertSectionAsync(connection, transaction, doc.Id.Value, section, doc.Title);
        }

        // Insert refs
        foreach (var @ref in refs)
        {
            await InsertRefAsync(connection, transaction, doc.Id.Value, @ref);
        }

        await transaction.CommitAsync();
    }

    public async Task DeleteAsync(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);

        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        // FTS delete for sections
        await DeleteSectionsAndRefsAsync(connection, transaction, docId);

        // Delete doc (cascades to sections, refs, relations, audit)
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM docs WHERE id = $id;";
            AddParameter(command, "$id", docId);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task RebuildAsync(IEnumerable<DocRebuildItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();

        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        // Clear all
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM docs;";
            await command.ExecuteNonQueryAsync();
        }

        // Clear FTS (contentless FTS5)
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM docs_fts_porter;";
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM docs_fts_trigram;";
            await command.ExecuteNonQueryAsync();
        }

        // Re-insert
        foreach (var item in list)
        {
            await UpsertDocAsync(connection, transaction, item.Doc);
            foreach (var section in item.Sections)
            {
                await InsertSectionAsync(connection, transaction, item.Doc.Id.Value, section, item.Doc.Title);
            }
            foreach (var @ref in item.Refs)
            {
                await InsertRefAsync(connection, transaction, item.Doc.Id.Value, @ref);
            }
        }

        await transaction.CommitAsync();
    }

    public async Task<Doc?> GetAsync(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);

        await using var connection = await OpenConnectionAsync();
        DocRow? row;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, ownership, origin, rel_path, kind, title, status, topic, read_order,
                       superseded_by, steps_total, steps_done, file_hash, mtime, indexed_at,
                       frontmatter_error, frontmatter_error_msg
                FROM docs WHERE id = $id;
                """;
            AddParameter(command, "$id", docId);
            await using var reader = await command.ExecuteReaderAsync();
            row = await reader.ReadAsync() ? ReadDocRow(reader) : null;
        }

        return row is null ? null : await HydrateDocAsync(connection, row.Value);
    }

    public async Task<DocSection?> GetSectionAsync(string docId, string anchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT doc_id, anchor, heading, level, line_start, line_end, status, annotation, steps_total, steps_done
            FROM doc_sections WHERE doc_id = $docId AND anchor = $anchor;
            """;
        AddParameter(command, "$docId", docId);
        AddParameter(command, "$anchor", anchor);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSectionRow(reader) : null;
    }

    public async Task<IReadOnlyList<DocSection>> GetSectionsAsync(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);

        await using var connection = await OpenConnectionAsync();
        var sections = new List<DocSection>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT doc_id, anchor, heading, level, line_start, line_end, status, annotation, steps_total, steps_done
            FROM doc_sections WHERE doc_id = $docId ORDER BY line_start;
            """;
        AddParameter(command, "$docId", docId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sections.Add(ReadSectionRow(reader));
        }
        return sections;
    }

    public async Task<IReadOnlyList<DocRef>> GetRefsAsync(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);

        await using var connection = await OpenConnectionAsync();
        var refs = new List<DocRef>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT doc_id, anchor, raw_text, ref_kind, resolved_path, resolved_line, resolved, resolved_at
            FROM doc_refs WHERE doc_id = $docId;
            """;
        AddParameter(command, "$docId", docId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            refs.Add(ReadRefRow(reader));
        }
        return refs;
    }

    public async Task<IReadOnlyList<DocRelation>> GetRelationsAsync(string docId, bool asSource = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);

        await using var connection = await OpenConnectionAsync();
        var relations = new List<DocRelation>();
        await using var command = connection.CreateCommand();
        command.CommandText = asSource
            ? "SELECT source_id, target_id, kind FROM doc_relations WHERE source_id = $id;"
            : "SELECT source_id, target_id, kind FROM doc_relations WHERE target_id = $id;";
        AddParameter(command, "$id", docId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relations.Add(ReadRelationRow(reader));
        }
        return relations;
    }

    public async Task UpsertRelationAsync(DocRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO doc_relations (source_id, target_id, kind)
            VALUES ($source, $target, $kind);
            """;
        AddParameter(command, "$source", relation.SourceId.Value);
        AddParameter(command, "$target", relation.TargetId.Value);
        AddParameter(command, "$kind", (int)relation.Kind);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteRelationAsync(string sourceId, string targetId, RelationKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM doc_relations WHERE source_id = $source AND target_id = $target AND kind = $kind;";
        AddParameter(command, "$source", sourceId);
        AddParameter(command, "$target", targetId);
        AddParameter(command, "$kind", (int)kind);
        await command.ExecuteNonQueryAsync();
    }

    public async Task WriteAuditAsync(DocAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO doc_audit (id, doc_id, actor, action, before_json, after_json, at)
            VALUES ($id, $docId, $actor, $action, $before, $after, $at);
            """;
        AddParameter(command, "$id", entry.Id);
        AddParameter(command, "$docId", entry.DocId);
        AddParameter(command, "$actor", entry.Actor);
        AddParameter(command, "$action", entry.Action);
        AddParameter(command, "$before", entry.BeforeJson);
        AddParameter(command, "$after", entry.AfterJson);
        AddParameter(command, "$at", entry.At.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<DocAuditEntry>> GetAuditAsync(string? docId = null, int limit = 100)
    {
        await using var connection = await OpenConnectionAsync();
        var entries = new List<DocAuditEntry>();
        await using var command = connection.CreateCommand();
        
        if (docId is null)
        {
            command.CommandText = "SELECT id, doc_id, actor, action, before_json, after_json, at FROM doc_audit ORDER BY at DESC LIMIT $limit;";
        }
        else
        {
            command.CommandText = "SELECT id, doc_id, actor, action, before_json, after_json, at FROM doc_audit WHERE doc_id = $docId ORDER BY at DESC LIMIT $limit;";
            AddParameter(command, "$docId", docId);
        }
        AddParameter(command, "$limit", limit);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(ReadAuditRow(reader));
        }
        return entries;
    }

    public async Task<DocPolicy?> GetPolicyAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value, updated_at FROM doc_policy WHERE key = $key;";
        AddParameter(command, "$key", key);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new DocPolicy(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)));
        }
        return null;
    }

    public async Task SetPolicyAsync(DocPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO doc_policy (key, value, updated_at)
            VALUES ($key, $value, $updatedAt);
            """;
        AddParameter(command, "$key", policy.Key);
        AddParameter(command, "$value", policy.Value);
        AddParameter(command, "$updatedAt", policy.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<DocOfferState?> GetOfferStateAsync(string projectHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectHash);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_hash, state, asked_at, answered_at, specs_since_ask FROM doc_offer WHERE project_hash = $hash;";
        AddParameter(command, "$hash", projectHash);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new DocOfferState(
                reader.GetString(0),
                Enum.Parse<OfferState>(reader.GetString(1)),
                reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetInt32(4));
        }
        return null;
    }

    public async Task SetOfferStateAsync(DocOfferState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO doc_offer (project_hash, state, asked_at, answered_at, specs_since_ask)
            VALUES ($hash, $state, $asked, $answered, $specs);
            """;
        AddParameter(command, "$hash", state.ProjectHash);
        AddParameter(command, "$state", state.State.ToString());
        AddParameter(command, "$asked", state.AskedAt?.ToString("O"));
        AddParameter(command, "$answered", state.AnsweredAt?.ToString("O"));
        AddParameter(command, "$specs", state.SpecsSinceAsk);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<DocSearchResult>> SearchAsync(
        string query,
        DocOwnership? ownership = null,
        DocOrigin? origin = null,
        DocKind? kind = null,
        string? topic = null,
        string? pathPrefix = null,
        int limit = 10)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));

        // Handle empty/whitespace query gracefully
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<DocSearchResult>();

        var tokens = ExtractTokens(query);
        if (tokens.Length == 0) return Array.Empty<DocSearchResult>();

        // Phase 1: AND on porter
        var andQuery = BuildPorterQuery(tokens, and: true);
        var hits = andQuery.Length > 0
            ? await SearchFtsAsync(andQuery, ownership, origin, kind, topic, pathPrefix, limit, useTrigram: false)
            : Array.Empty<DocSearchResult>();

        if (hits.Count >= AndToOrThreshold) return hits;

        // Phase 2: OR fallback on porter
        var orQuery = BuildPorterQuery(tokens, and: false);
        hits = orQuery.Length > 0
            ? await SearchFtsAsync(orQuery, ownership, origin, kind, topic, pathPrefix, limit, useTrigram: false)
            : Array.Empty<DocSearchResult>();

        if (hits.Count >= AndToOrThreshold) return hits;

        // Phase 3: Trigram fallback (broader recall)
        var trigramQuery = BuildTrigramQuery(tokens);
        return trigramQuery.Length > 0
            ? await SearchFtsAsync(trigramQuery, ownership, origin, kind, topic, pathPrefix, limit, useTrigram: true)
            : Array.Empty<DocSearchResult>();
    }

    public async Task<IReadOnlyList<DocListItem>> ListAsync(
        DocOwnership? ownership = null,
        DocOrigin? origin = null,
        DocKind? kind = null,
        string? topic = null,
        string sort = "topic")
    {
        var where = new List<string>();
        if (ownership.HasValue) where.Add("ownership = $ownership");
        if (origin.HasValue) where.Add("origin = $origin");
        if (kind.HasValue) where.Add("kind = $kind");
        if (!string.IsNullOrWhiteSpace(topic)) where.Add("topic = $topic");

        var whereClause = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty;
        var orderBy = sort switch
        {
            "topic" => "topic, read_order, rel_path",
            "title" => "title",
            "updated" => "indexed_at DESC",
            _ => "topic, read_order, rel_path"
        };

        await using var connection = await OpenConnectionAsync();
        var results = new List<DocListItem>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, ownership, origin, rel_path, kind, title, status, topic, read_order, steps_total, steps_done
            FROM docs{whereClause} ORDER BY {orderBy};
            """;
        if (ownership.HasValue) AddParameter(command, "$ownership", ownership.Value.ToString().ToLowerInvariant());
        if (origin.HasValue) AddParameter(command, "$origin", origin.Value.ToString().ToLowerInvariant());
        if (kind.HasValue) AddParameter(command, "$kind", kind.Value.ToString().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(topic)) AddParameter(command, "$topic", topic);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DocListItem(
                reader.GetString(0),
                Enum.Parse<DocOwnership>(reader.GetString(1), true),
                Enum.Parse<DocOrigin>(reader.GetString(2), true),
                reader.GetString(3),
                Enum.Parse<DocKind>(reader.GetString(4), true),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10)));
        }
        return results;
    }

    private async Task UpsertDocAsync(SqliteConnection connection, SqliteTransaction transaction, Doc doc)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO docs (
                id, ownership, origin, rel_path, kind, title, status, topic, read_order,
                superseded_by, steps_total, steps_done, file_hash, mtime, indexed_at,
                frontmatter_error, frontmatter_error_msg)
            VALUES (
                $id, $ownership, $origin, $relPath, $kind, $title, $status, $topic, $order,
                $supersededBy, $stepsTotal, $stepsDone, $fileHash, $mtime, $indexedAt,
                $fmError, $fmErrorMsg)
            ON CONFLICT(id) DO UPDATE SET
                ownership = excluded.ownership,
                origin = excluded.origin,
                rel_path = excluded.rel_path,
                kind = excluded.kind,
                title = excluded.title,
                status = excluded.status,
                topic = excluded.topic,
                read_order = excluded.read_order,
                superseded_by = excluded.superseded_by,
                steps_total = excluded.steps_total,
                steps_done = excluded.steps_done,
                file_hash = excluded.file_hash,
                mtime = excluded.mtime,
                indexed_at = excluded.indexed_at,
                frontmatter_error = excluded.frontmatter_error,
                frontmatter_error_msg = excluded.frontmatter_error_msg;
            """;
        AddParameter(command, "$id", doc.Id.Value);
        AddParameter(command, "$ownership", doc.Ownership.ToString().ToLowerInvariant());
        AddParameter(command, "$origin", doc.Origin.ToString().ToLowerInvariant());
        AddParameter(command, "$relPath", doc.RelPath);
        AddParameter(command, "$kind", doc.Kind.ToString().ToLowerInvariant());
        AddParameter(command, "$title", doc.Title);
        AddParameter(command, "$status", doc.Status.Value);
        AddParameter(command, "$topic", doc.Topic);
        AddParameter(command, "$order", doc.Order);
        AddParameter(command, "$supersededBy", doc.SupersededBy);
        AddParameter(command, "$stepsTotal", doc.StepsTotal);
        AddParameter(command, "$stepsDone", doc.StepsDone);
        AddParameter(command, "$fileHash", doc.FileHash);
        AddParameter(command, "$mtime", doc.MTime.ToString("O"));
        AddParameter(command, "$indexedAt", doc.IndexedAt.ToString("O"));
        AddParameter(command, "$fmError", doc.FrontMatterError ? 1 : 0);
        AddParameter(command, "$fmErrorMsg", doc.FrontMatterErrorMessage);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteSectionsAndRefsOnlyAsync(SqliteConnection connection, SqliteTransaction transaction, string docId)
    {
        // Get sections for FTS delete (contentless FTS5 requires manual delete)
        var sections = new List<(long RowId, string Heading)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT rowid, heading FROM doc_sections WHERE doc_id = $docId;";
            AddParameter(command, "$docId", docId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sections.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        // FTS delete for porter (contentless FTS5)
        foreach (var s in sections)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM docs_fts_porter WHERE rowid = $rowid;";
            AddParameter(cmd, "$rowid", s.RowId);
            await cmd.ExecuteNonQueryAsync();
        }

        // FTS delete for trigram (contentless FTS5)
        foreach (var s in sections)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM docs_fts_trigram WHERE rowid = $rowid;";
            AddParameter(cmd, "$rowid", s.RowId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Delete sections and refs (cascading FK handles refs, relations, audit)
        // Note: We do NOT delete the doc here - that's the caller's responsibility
        await using var delSections = connection.CreateCommand();
        delSections.Transaction = transaction;
        delSections.CommandText = "DELETE FROM doc_sections WHERE doc_id = $id;";
        AddParameter(delSections, "$id", docId);
        await delSections.ExecuteNonQueryAsync();
    }

    private async Task DeleteSectionsAndRefsAsync(SqliteConnection connection, SqliteTransaction transaction, string docId)
    {
        // Delete sections/refs with FTS sync
        await DeleteSectionsAndRefsOnlyAsync(connection, transaction, docId);

        // Delete the doc itself (cascades to relations, audit)
        await using var del = connection.CreateCommand();
        del.Transaction = transaction;
        del.CommandText = "DELETE FROM docs WHERE id = $id;";
        AddParameter(del, "$id", docId);
        await del.ExecuteNonQueryAsync();
    }

    private async Task InsertSectionAsync(SqliteConnection connection, SqliteTransaction transaction, string docId, DocSection section, string docTitle)
    {
        // Insert section (rowid will be auto)
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO doc_sections (doc_id, anchor, heading, level, line_start, line_end, status, annotation, steps_total, steps_done)
            VALUES ($docId, $anchor, $heading, $level, $lineStart, $lineEnd, $status, $annotation, $stepsTotal, $stepsDone);
            """;
        AddParameter(cmd, "$docId", docId);
        AddParameter(cmd, "$anchor", section.Anchor);
        AddParameter(cmd, "$heading", section.Heading);
        AddParameter(cmd, "$level", section.Level);
        AddParameter(cmd, "$lineStart", section.LineStart);
        AddParameter(cmd, "$lineEnd", section.LineEnd);
        AddParameter(cmd, "$status", section.Status.Value);
        AddParameter(cmd, "$annotation", section.Annotation);
        AddParameter(cmd, "$stepsTotal", section.StepsTotal);
        AddParameter(cmd, "$stepsDone", section.StepsDone);
        await cmd.ExecuteNonQueryAsync();

        // Get rowid for FTS insert
        long rowId;
        await using (var cmd2 = connection.CreateCommand())
        {
            cmd2.Transaction = transaction;
            cmd2.CommandText = "SELECT last_insert_rowid();";
            rowId = (long)(await cmd2.ExecuteScalarAsync())!;
        }

        // FTS insert for porter (contentless FTS5 - explicit rowid)
        await using var fts1 = connection.CreateCommand();
        fts1.Transaction = transaction;
        fts1.CommandText = "INSERT INTO docs_fts_porter(rowid, title, heading, body) VALUES ($rowid, $title, $heading, $body);";
        AddParameter(fts1, "$rowid", rowId);
        AddParameter(fts1, "$title", docTitle);
        AddParameter(fts1, "$heading", section.Heading);
        AddParameter(fts1, "$body", ""); // Body not stored in section table
        await fts1.ExecuteNonQueryAsync();

        // FTS insert for trigram (contentless FTS5 - explicit rowid)
        await using var fts2 = connection.CreateCommand();
        fts2.Transaction = transaction;
        fts2.CommandText = "INSERT INTO docs_fts_trigram(rowid, title, heading, body) VALUES ($rowid, $title, $heading, $body);";
        AddParameter(fts2, "$rowid", rowId);
        AddParameter(fts2, "$title", docTitle);
        AddParameter(fts2, "$heading", section.Heading);
        AddParameter(fts2, "$body", "");
        await fts2.ExecuteNonQueryAsync();
    }

    private async Task InsertRefAsync(SqliteConnection connection, SqliteTransaction transaction, string docId, DocRef @ref)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR IGNORE INTO doc_refs (doc_id, anchor, raw_text, ref_kind, resolved_path, resolved_line, resolved, resolved_at)
            VALUES ($docId, $anchor, $raw, $refKind, $resolvedPath, $resolvedLine, $resolved, $resolvedAt);
            """;
        AddParameter(cmd, "$docId", docId);
        AddParameter(cmd, "$anchor", @ref.Anchor);
        AddParameter(cmd, "$raw", @ref.RawText);
        AddParameter(cmd, "$refKind", (int)@ref.RefKind);
        AddParameter(cmd, "$resolvedPath", @ref.ResolvedPath);
        AddParameter(cmd, "$resolvedLine", @ref.ResolvedLine);
        AddParameter(cmd, "$resolved", @ref.Resolved ? 1 : 0);
        AddParameter(cmd, "$resolvedAt", @ref.ResolvedAt?.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<DocSearchResult>> SearchFtsAsync(
        string ftsQuery,
        DocOwnership? ownership,
        DocOrigin? origin,
        DocKind? kind,
        string? topic,
        string? pathPrefix,
        int limit,
        bool useTrigram)
    {
        var ftsTable = useTrigram ? "docs_fts_trigram" : "docs_fts_porter";

        var where = new List<string> { $"{ftsTable} MATCH $query" };
        if (ownership.HasValue) where.Add("d.ownership = $ownership");
        if (origin.HasValue) where.Add("d.origin = $origin");
        if (kind.HasValue) where.Add("d.kind = $kind");
        if (!string.IsNullOrWhiteSpace(topic)) where.Add("d.topic = $topic");
        if (!string.IsNullOrWhiteSpace(pathPrefix)) where.Add("d.rel_path LIKE $pathPrefix");

        var whereClause = " WHERE " + string.Join(" AND ", where);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.id, d.ownership, d.origin, d.rel_path, d.kind, d.title, d.status, d.topic,
                   d.steps_total, d.steps_done, bm25({ftsTable}) AS rank
            FROM {ftsTable}
            JOIN doc_sections s ON s.rowid = {ftsTable}.rowid
            JOIN docs d ON d.id = s.doc_id
            {whereClause}
            ORDER BY rank
            LIMIT $limit;
            """;
        AddParameter(command, "$query", ftsQuery);
        if (ownership.HasValue) AddParameter(command, "$ownership", ownership.Value.ToString().ToLowerInvariant());
        if (origin.HasValue) AddParameter(command, "$origin", origin.Value.ToString().ToLowerInvariant());
        if (kind.HasValue) AddParameter(command, "$kind", kind.Value.ToString().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(topic)) AddParameter(command, "$topic", topic);
        if (!string.IsNullOrWhiteSpace(pathPrefix)) AddParameter(command, "$pathPrefix", pathPrefix + "%");
        AddParameter(command, "$limit", limit);

        var results = new List<DocSearchResult>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // BM25 returns negative ranks; more-negative = better. Invert so higher = better.
            var rank = -reader.GetDouble(10);
            results.Add(new DocSearchResult(
                reader.GetString(0),
                Enum.Parse<DocOwnership>(reader.GetString(1), true),
                Enum.Parse<DocOrigin>(reader.GetString(2), true),
                reader.GetString(3),
                Enum.Parse<DocKind>(reader.GetString(4), true),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                rank));
        }
        return results;
    }

    private DocRow ReadDocRow(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7),
            reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt32(10), reader.GetInt32(11), reader.GetString(12), reader.GetString(13),
            reader.GetString(14), reader.GetInt32(15), reader.IsDBNull(16) ? null : reader.GetString(16));

    private DocSection ReadSectionRow(DbDataReader reader) =>
        new(DocId.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), new DocStatusValue((DocKind)0, reader.GetInt32(6)), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8), reader.GetInt32(9));

    private DocRef ReadRefRow(DbDataReader reader) =>
        new(DocId.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            (ReferenceKind)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.GetInt32(6) == 1)
        {
            ResolvedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))
        };

    private DocRelation ReadRelationRow(DbDataReader reader) =>
        new(DocId.Parse(reader.GetString(0)), DocId.Parse(reader.GetString(1)), (RelationKind)reader.GetInt32(2));

    private DocAuditEntry ReadAuditRow(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)));

    private async Task<Doc> HydrateDocAsync(SqliteConnection connection, DocRow row)
    {
        var sections = await GetSectionsAsync(row.Id);
        var refs = await GetRefsAsync(row.Id);
        return new Doc(
            DocId.Parse(row.Id),
            Enum.Parse<DocOwnership>(row.Ownership, true),
            Enum.Parse<DocOrigin>(row.Origin, true),
            row.RelPath,
            Enum.Parse<DocKind>(row.Kind, true),
            row.Title,
            new DocStatusValue(Enum.Parse<DocKind>(row.Kind, true), row.Status),
            row.Topic,
            row.Order,
            row.SupersededBy,
            row.StepsTotal,
            row.StepsDone,
            row.FileHash,
            DateTimeOffset.Parse(row.MTime),
            DateTimeOffset.Parse(row.IndexedAt),
            row.FrontMatterError == 1,
            row.FrontMatterErrorMsg);
    }

    private static string[] ExtractTokens(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", string.Empty).Trim().ToLowerInvariant())
            .Where(t => t.Length > 0 && t.Any(char.IsLetterOrDigit))
            .ToArray();

    private static string BuildPorterQuery(string[] tokens, bool and)
    {
        var phrases = tokens.Where(t => t.Length >= 2).Select(t => $"\"{t}\"").ToList();
        if (phrases.Count == 0) return string.Empty;
        return and ? string.Join(' ', phrases) : string.Join(" OR ", phrases);
    }

    private static string BuildTrigramQuery(string[] tokens) =>
        string.Join(" OR ", tokens.Where(t => t.Length >= 2).Select(t => $"\"{t}\""));

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync();
        }
        await EnsureInitializedAsync(connection);
        return connection;
    }

    private async Task EnsureInitializedAsync(DbConnection connection)
    {
        if (_schemaReady) return;
        await _initGate.WaitAsync();
        try
        {
            if (_schemaReady) return;
            await EnsureSchemaAsync(connection);
            _schemaReady = true;
        }
        finally { _initGate.Release(); }
    }

    private static async Task EnsureSchemaAsync(DbConnection connection)
    {
        foreach (var sql in new[]
        {
            CreateDocsSql, CreateSectionsSql, CreateRefsSql, CreateRelationsSql,
            CreateAuditSql, CreatePolicySql, CreateOfferSql,
            CreateFtsPorterSql, CreateFtsTrigramSql
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        // Self-heal: rebuild FTS if empty but sections exist (contentless FTS5)
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM doc_sections;";
        var sectionCount = (long)(await check.ExecuteScalarAsync())!;
        if (sectionCount > 0)
        {
            await using var fts1 = connection.CreateCommand();
            fts1.CommandText = "SELECT COUNT(*) FROM docs_fts_porter;";
            var fts1Count = (long)(await fts1.ExecuteScalarAsync())!;
            if (fts1Count == 0)
            {
                // Rebuild porter FTS from doc_sections
                await using var rebuild = connection.CreateCommand();
                rebuild.CommandText = """
                    INSERT INTO docs_fts_porter(rowid, title, heading, body)
                    SELECT rowid, '', heading, '' FROM doc_sections;
                    """;
                await rebuild.ExecuteNonQueryAsync();
            }

            await using var fts2 = connection.CreateCommand();
            fts2.CommandText = "SELECT COUNT(*) FROM docs_fts_trigram;";
            var fts2Count = (long)(await fts2.ExecuteScalarAsync())!;
            if (fts2Count == 0)
            {
                // Rebuild trigram FTS from doc_sections
                await using var rebuild = connection.CreateCommand();
                rebuild.CommandText = """
                    INSERT INTO docs_fts_trigram(rowid, title, heading, body)
                    SELECT rowid, '', heading, '' FROM doc_sections;
                    """;
                await rebuild.ExecuteNonQueryAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    private readonly record struct DocRow(
        string Id, string Ownership, string Origin, string RelPath, string Kind, string Title,
        int Status, string Topic, int Order, string? SupersededBy, int StepsTotal, int StepsDone,
        string FileHash, string MTime, string IndexedAt, int FrontMatterError, string? FrontMatterErrorMsg);
}