using System.Data.Common;
using Microsoft.Data.Sqlite;

using Eling.Core.Scope;

namespace Eling.Core.Skill.Storage;

/// <summary>
/// Dedicated <c>skill-index.db</c> FTS5 index for the skill catalog. Kept
/// separate from the memories/snippets <c>index.db</c> so the skill subsystem
/// owns its schema and migration lifecycle. External-content FTS5 table
/// (<c>content='skills', content_rowid='rowid'</c>) kept in sync manually —
/// delete + insert FTS rows in the same transaction as the upsert — which
/// supersedes the spec's trigger-based wording (mirrors
/// <c>SqliteMemoryIndex</c>).
/// </summary>
public sealed class SqliteSkillIndex : ISkillIndex
{
    // Schema verbatim from the skill-management design spec §5 (triggers replaced
    // by manual sync; tokenize adds remove_diacritics 1 per the task brief).
    private const string CreateSkillsSql = """
        CREATE TABLE IF NOT EXISTS skills (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            description TEXT NOT NULL,
            body TEXT NOT NULL,
            body_path TEXT NOT NULL,
            origin TEXT NOT NULL DEFAULT 'eling' CHECK (origin IN ('eling','external')),
            trashed_at TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL);
        """;

    private const string CreateTagsSql = """
        CREATE TABLE IF NOT EXISTS skill_tags (
            skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
            tag TEXT NOT NULL,
            PRIMARY KEY (skill_id, tag));
        """;

    private const string CreateRelatedMemoriesSql = """
        CREATE TABLE IF NOT EXISTS skill_related_memories (
            skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
            memory_id TEXT NOT NULL,
            memory_scope TEXT NOT NULL,
            relation TEXT NOT NULL,
            PRIMARY KEY (skill_id, memory_id, memory_scope));
        """;

    private const string CreateRelatedSnippetsSql = """
        CREATE TABLE IF NOT EXISTS skill_related_snippets (
            skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
            snippet_id TEXT NOT NULL,
            relation TEXT NOT NULL,
            PRIMARY KEY (skill_id, snippet_id));
        """;

    private const string CreateFtsSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS skills_fts USING fts5(
            name, description, body,
            content='skills', content_rowid='rowid',
            tokenize='porter unicode61 remove_diacritics 1');
        """;

    // External-content FTS5 delete command: removes the row's tokens using the
    // OLD column values. Must run before the skills row is deleted.
    private const string FtsDeleteSql = """
        INSERT INTO skills_fts(skills_fts, rowid, name, description, body)
        VALUES('delete', $rowid, $name, $description, $body);
        """;

    private const int AndToOrThreshold = 3;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _schemaReady;

    public SqliteSkillIndex(string? databasePath = null)
    {
        var path = databasePath ?? SkillCatalogPaths.IndexFilePath;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();
    }

    public async Task IndexAsync(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.Origin == SkillOrigin.External)
        {
            throw new ArgumentException(
                "External skills are indexed only via AdoptExternalScanResultAsync.",
                nameof(skill));
        }

        await UpsertAsync(skill, origin: SkillOrigin.Eling);
    }

    public Task AdoptExternalScanResultAsync(Skill skill, string? bodyPath = null)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return UpsertAsync(skill, origin: SkillOrigin.External, bodyPath);
    }

    public async Task RemoveAsync(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = await OpenConnectionAsync();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        var rows = await ReadExistingRowsAsync(connection, transaction, id: null, name);
        foreach (var row in rows)
        {
            await FtsDeleteAsync(connection, transaction, row);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM skills WHERE name = $name;";
            AddParameter(command, "$name", name);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task RebuildAsync(IEnumerable<Skill> skills, IReadOnlySet<string>? carryExternalNames = null)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var items = skills.ToList();

        await using var connection = await OpenConnectionAsync();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        // Capture rows to carry verbatim before wiping: trashed rows (a name
        // still held by a trashed skill is not un-blocked by a rebuild) and,
        // when the caller verified them, active external rows whose folder is
        // not kebab-case — their paths live only in body_path, so they cannot
        // be re-derived from the display name. A name reclaimed by a kept
        // skill wins over everything.
        var carry = await ReadCarryRowsAsync(connection, transaction, carryExternalNames);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // Cascading FKs drop skill_tags/skill_related_* with the parent rows.
            command.CommandText = "DELETE FROM skills;";
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // 'delete-all' clears the external-content FTS index completely.
            command.CommandText = "INSERT INTO skills_fts(skills_fts) VALUES('delete-all');";
            await command.ExecuteNonQueryAsync();
        }

        foreach (var skill in items)
        {
            await InsertSkillAsync(connection, transaction, skill, origin: skill.Origin, createdAt: null, trashedAt: null);
        }

        if (carry.Count > 0)
        {
            var keptNames = new HashSet<string>(items.Select(s => s.Name), StringComparer.Ordinal);

            foreach (var row in carry)
            {
                if (keptNames.Contains(row.Skill.Name))
                {
                    continue;
                }

                await InsertSkillAsync(
                    connection,
                    transaction,
                    row.Skill,
                    origin: row.Skill.Origin,
                    createdAt: row.Skill.CreatedAt,
                    trashedAt: row.TrashedAt,
                    bodyPath: row.BodyPath);
            }
        }

        await transaction.CommitAsync();
    }

    public async Task<Skill?> GetByNameAsync(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = await OpenConnectionAsync();
        SkillRow? row;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, description, body, origin, created_at, updated_at
                FROM skills WHERE name = $name;
                """;
            AddParameter(command, "$name", name);
            await using var reader = await command.ExecuteReaderAsync();
            row = await reader.ReadAsync() ? ReadSkillRow(reader) : null;
        }

        return row is null ? null : await HydrateAsync(connection, row.Value);
    }

    public async Task<IReadOnlyCollection<SkillSearchResult>> SearchAsync(
        string query, string? origin = null, int limit = 10, bool includeTrashed = false, string? tag = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be at least 1.");
        }

        var originWire = ParseOriginFilter(origin);
        var tagWire = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim().ToLowerInvariant();
        var tokens = ExtractTokens(query);
        if (tokens.Length == 0)
        {
            return Array.Empty<SkillSearchResult>();
        }

        // Phase 1: AND on porter. Default precision.
        var andQuery = BuildPorterQuery(tokens, and: true);
        var hits = andQuery.Length > 0
            ? await SearchPorterAsync(andQuery, originWire, limit, includeTrashed, tagWire)
            : Array.Empty<SkillSearchResult>();
        if (hits.Count >= AndToOrThreshold)
        {
            return hits;
        }

        // Phase 2: OR fallback — AND can return nothing when the term list is
        // broad; BM25 still ranks the OR results so the most cross-relevant
        // skills lead.
        var orQuery = BuildPorterQuery(tokens, and: false);
        return orQuery.Length > 0
            ? await SearchPorterAsync(orQuery, originWire, limit, includeTrashed, tagWire)
            : Array.Empty<SkillSearchResult>();
    }

    public async Task<IReadOnlyList<Skill>> ListAsync(bool includeTrashed, string? origin, string sort = "name")
    {
        var originWire = ParseOriginFilter(origin);
        var orderBy = sort switch
        {
            "name" => "name ASC",
            "updated_at" => "updated_at DESC",
            _ => throw new ArgumentException("Sort must be \"name\" or \"updated_at\".", nameof(sort)),
        };

        var where = new List<string>();
        if (!includeTrashed)
        {
            where.Add("trashed_at IS NULL");
        }

        if (originWire is not null)
        {
            where.Add("origin = $origin");
        }

        var whereClause = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty;

        await using var connection = await OpenConnectionAsync();
        var rows = new List<SkillRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, name, description, body, origin, created_at, updated_at
                FROM skills{whereClause} ORDER BY {orderBy};
                """;
            if (originWire is not null)
            {
                AddParameter(command, "$origin", originWire);
            }

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(ReadSkillRow(reader));
            }
        }

        var results = new List<Skill>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(await HydrateAsync(connection, row));
        }

        return results;
    }

    public async Task MarkTrashedAsync(string name, bool trashed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = trashed
            ? "UPDATE skills SET trashed_at = $now WHERE name = $name AND trashed_at IS NULL;"
            : "UPDATE skills SET trashed_at = NULL WHERE name = $name AND trashed_at IS NOT NULL;";
        AddParameter(command, "$name", name);
        if (trashed)
        {
            AddParameter(command, "$now", DateTimeOffset.UtcNow.ToString("O"));
        }

        var changed = await command.ExecuteNonQueryAsync();
        if (changed == 0)
        {
            // Either the name is unknown or the row is already in the requested state.
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM skills WHERE name = $name;";
            AddParameter(exists, "$name", name);
            var count = (long)(await exists.ExecuteScalarAsync())!;
            if (count == 0)
            {
                throw new KeyNotFoundException($"Skill '{name}' is not in the skill index.");
            }
        }
    }

    public async Task<bool> IsNameTakenAsync(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM skills WHERE name = $name;";
        AddParameter(command, "$name", name);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    public async Task<IReadOnlyDictionary<string, string>> ListPathsAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, body_path FROM skills;";

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            paths[reader.GetString(0)] = reader.GetString(1);
        }

        return paths;
    }

    private async Task UpsertAsync(Skill skill, SkillOrigin origin, string? bodyPath = null)
    {
        await using var connection = await OpenConnectionAsync();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        // Delete any row holding the same id OR the same name (they can be two
        // different rows), so the insert below can never violate the UNIQUE(name)
        // or PRIMARY KEY(id) constraints.
        var existing = await ReadExistingRowsAsync(connection, transaction, skill.Id.Value, skill.Name);
        var idRow = existing
            .Where(r => string.Equals(r.Id, skill.Id.Value, StringComparison.Ordinal))
            .Select(r => (ExistingRow?)r)
            .FirstOrDefault();
        var nameRow = existing
            .Where(r => string.Equals(r.Name, skill.Name, StringComparison.Ordinal))
            .Select(r => (ExistingRow?)r)
            .FirstOrDefault();
        var createdAt = (idRow?.CreatedAt ?? nameRow?.CreatedAt) is { } created ? DateTimeOffset.Parse(created) : (DateTimeOffset?)null;
        // A trashed row matched only by name keeps its id (it is the row being upserted) -
        // the trash marker must survive external re-adoption too, never just fresh rows.
        var trashedAt = idRow?.TrashedAt ?? nameRow?.TrashedAt;

        foreach (var row in existing)
        {
            await FtsDeleteAsync(connection, transaction, row);
        }

        if (existing.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM skills WHERE id = $id OR name = $name;";
            AddParameter(command, "$id", skill.Id.Value);
            AddParameter(command, "$name", skill.Name);
            await command.ExecuteNonQueryAsync();
        }

        await InsertSkillAsync(connection, transaction, skill, origin, createdAt, trashedAt, bodyPath);
        await transaction.CommitAsync();
    }

    private static async Task InsertSkillAsync(
        DbConnection connection,
        DbTransaction transaction,
        Skill skill,
        SkillOrigin origin,
        DateTimeOffset? createdAt,
        string? trashedAt,
        string? bodyPath = null)
    {
        bodyPath ??= $"skills/{skill.Name}/SKILL.md";
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO skills (id, name, description, body, body_path, origin, trashed_at, created_at, updated_at)
                VALUES ($id, $name, $description, $body, $bodyPath, $origin, $trashedAt, $createdAt, $updatedAt);
                """;
            AddParameter(command, "$id", skill.Id.Value);
            AddParameter(command, "$name", skill.Name);
            AddParameter(command, "$description", skill.Description);
            AddParameter(command, "$body", skill.Body);
            AddParameter(command, "$bodyPath", bodyPath);
            AddParameter(command, "$origin", origin.WireName);
            AddParameter(command, "$trashedAt", trashedAt);
            AddParameter(command, "$createdAt", (createdAt ?? skill.CreatedAt).ToString("O"));
            AddParameter(command, "$updatedAt", skill.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        long rowId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT last_insert_rowid();";
            rowId = (long)(await command.ExecuteScalarAsync())!;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO skills_fts(rowid, name, description, body)
                VALUES ($rowid, $name, $description, $body);
                """;
            AddParameter(command, "$rowid", rowId);
            AddParameter(command, "$name", skill.Name);
            AddParameter(command, "$description", skill.Description);
            AddParameter(command, "$body", skill.Body);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var tag in skill.Tags)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO skill_tags (skill_id, tag) VALUES ($id, $tag);";
            AddParameter(command, "$id", skill.Id.Value);
            AddParameter(command, "$tag", tag);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var reference in skill.RelatedMemories)
        {
            if (string.IsNullOrWhiteSpace(reference.Scope))
            {
                throw new ArgumentException(
                    $"Related memory '{reference.Id}' is missing a scope label.",
                    nameof(skill));
            }

            if (!SkillVocabulary.IsMemoryScope(reference.Scope))
            {
                throw new ArgumentException(
                    $"Related memory '{reference.Id}' uses unknown scope '{reference.Scope}'.",
                    nameof(skill));
            }

            if (!SkillVocabulary.IsRelation(reference.Relation))
            {
                throw new ArgumentException(
                    $"Related memory '{reference.Id}' uses unknown relation '{reference.Relation}'.",
                    nameof(skill));
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO skill_related_memories (skill_id, memory_id, memory_scope, relation)
                VALUES ($id, $memoryId, $scope, $relation);
                """;
            AddParameter(command, "$id", skill.Id.Value);
            AddParameter(command, "$memoryId", reference.Id);
            AddParameter(command, "$scope", reference.Scope);
            AddParameter(command, "$relation", reference.Relation);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var reference in skill.RelatedSnippets)
        {
            if (!SkillVocabulary.IsRelation(reference.Relation))
            {
                throw new ArgumentException(
                    $"Related snippet '{reference.Id}' uses unknown relation '{reference.Relation}'.",
                    nameof(skill));
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO skill_related_snippets (skill_id, snippet_id, relation)
                VALUES ($id, $snippetId, $relation);
                """;
            AddParameter(command, "$id", skill.Id.Value);
            AddParameter(command, "$snippetId", reference.Id);
            AddParameter(command, "$relation", reference.Relation);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<List<ExistingRow>> ReadExistingRowsAsync(
        DbConnection connection, DbTransaction transaction, string? id, string name)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rowid, id, name, description, body, created_at, trashed_at
            FROM skills
            WHERE ($id IS NOT NULL AND id = $id) OR name = $name;
            """;
        AddParameter(command, "$id", id);
        AddParameter(command, "$name", name);
        var rows = new List<ExistingRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ExistingRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private static async Task FtsDeleteAsync(DbConnection connection, DbTransaction transaction, ExistingRow row)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FtsDeleteSql;
        AddParameter(command, "$rowid", row.RowId);
        AddParameter(command, "$name", row.Name);
        AddParameter(command, "$description", row.Description);
        AddParameter(command, "$body", row.Body);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyCollection<SkillSearchResult>> SearchPorterAsync(
        string ftsQuery, string? originWire, int limit, bool includeTrashed, string? tagWire)
    {
        var where = new List<string> { "skills_fts MATCH $query" };
        if (!includeTrashed)
        {
            where.Add("s.trashed_at IS NULL");
        }

        if (originWire is not null)
        {
            where.Add("s.origin = $origin");
        }

        if (tagWire is not null)
        {
            // The tag filter lives in the same statement (not a post-filter over
            // hydrated rows), so LIMIT applies after filtering.
            where.Add("EXISTS (SELECT 1 FROM skill_tags t WHERE t.skill_id = s.id AND t.tag = $tag)");
        }

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, bm25(skills_fts) AS rank, s.origin, s.body_path, s.updated_at, s.name, s.description
            FROM skills_fts
            JOIN skills s ON s.rowid = skills_fts.rowid
            WHERE {string.Join(" AND ", where)}
            ORDER BY rank
            LIMIT $limit;
            """;
        AddParameter(command, "$query", ftsQuery);
        if (originWire is not null)
        {
            AddParameter(command, "$origin", originWire);
        }

        if (tagWire is not null)
        {
            AddParameter(command, "$tag", tagWire);
        }

        AddParameter(command, "$limit", limit);

        var results = new List<SkillSearchResult>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // BM25 returns negative ranks; more-negative = better. Invert so higher = better.
            var rank = -reader.GetDouble(1);
            var origin = reader.GetString(2) == SkillOrigin.External.WireName ? SkillOrigin.External : SkillOrigin.Eling;
            results.Add(new SkillSearchResult(
                new SkillId(reader.GetString(0)),
                rank,
                origin,
                ToAbsolutePath(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return results;
    }

    private static string ToAbsolutePath(string bodyPath) =>
        Path.GetFullPath(Path.Combine(
            SkillCatalogPaths.CatalogDirectory,
            bodyPath.Replace('/', Path.DirectorySeparatorChar)));

    private static string[] ExtractTokens(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", string.Empty).Trim().ToLowerInvariant())
            .Where(t => t.Length > 0 && t.Any(char.IsLetterOrDigit))
            .ToArray();

    private static string BuildPorterQuery(string[] tokens, bool and)
    {
        var phrases = tokens
            .Where(t => t.Length >= 2)
            .Select(t => $"\"{t}\"")
            .ToList();
        if (phrases.Count == 0)
        {
            return string.Empty;
        }

        return and ? string.Join(' ', phrases) : string.Join(" OR ", phrases);
    }

    private static string? ParseOriginFilter(string? origin)
    {
        if (origin is null)
        {
            return null;
        }

        return origin.Trim().ToLowerInvariant() switch
        {
            "eling" => SkillOrigin.Eling.WireName,
            "external" => SkillOrigin.External.WireName,
            _ => throw new ArgumentException($"Origin must be \"eling\" or \"external\", not \"{origin}\".", nameof(origin)),
        };
    }

    private static SkillRow ReadSkillRow(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6));

    private async Task<Skill> HydrateAsync(
        DbConnection connection,
        SkillRow row)
    {
        var tags = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT tag FROM skill_tags WHERE skill_id = $id ORDER BY tag;";
            AddParameter(command, "$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tags.Add(reader.GetString(0));
            }
        }

        var relatedMemories = new List<RelatedReference>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT memory_id, memory_scope, relation FROM skill_related_memories WHERE skill_id = $id;";
            AddParameter(command, "$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                relatedMemories.Add(new RelatedReference(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var relatedSnippets = new List<RelatedReference>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT snippet_id, relation FROM skill_related_snippets WHERE skill_id = $id;";
            AddParameter(command, "$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                relatedSnippets.Add(new RelatedReference(reader.GetString(0), null, reader.GetString(1)));
            }
        }

        var origin = row.Origin == SkillOrigin.External.WireName ? SkillOrigin.External : SkillOrigin.Eling;
        return new Skill(
            row.Name,
            row.Description,
            row.Body,
            tags,
            relatedMemories,
            relatedSnippets,
            source: null,
            id: new SkillId(row.Id),
            origin: origin,
            createdAt: DateTimeOffset.Parse(row.CreatedAt),
            updatedAt: DateTimeOffset.Parse(row.UpdatedAt));
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        // The parent directory may not exist yet (e.g. fresh machine). Create it
        // lazily on first use so reads before the first write open an empty index.
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

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
        if (_schemaReady)
        {
            return;
        }

        await _initGate.WaitAsync();
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await EnsureSchemaAsync(connection);
            _schemaReady = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private static async Task EnsureSchemaAsync(DbConnection connection)
    {
        foreach (var sql in new[] { CreateSkillsSql, CreateTagsSql, CreateRelatedMemoriesSql, CreateRelatedSnippetsSql, CreateFtsSql })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        // Self-heal: if the relational table has rows but the FTS index is empty
        // (e.g. the FTS table was dropped/recreated out of band), rebuild it.
        var ftsRows = await CountRowsAsync(connection, "skills_fts");
        var skillRows = await CountRowsAsync(connection, "skills");
        if (ftsRows == 0 && skillRows > 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO skills_fts(skills_fts) VALUES('rebuild');";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> CountRowsAsync(DbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        var result = await command.ExecuteScalarAsync();
        return result is long count ? count : 0;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record TrashRow(
        Skill Skill,
        string? BodyPath,
        string? TrashedAt);

    private sealed record TrashCandidate(
        SkillRow Row,
        string? BodyPath,
        string? TrashedAt);

    private static TrashCandidate ReadCarryRow(DbDataReader reader)
        => new(new SkillRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));

    private async Task<IReadOnlyList<TrashRow>> ReadCarryRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlySet<string>? carryExternalNames)
    {
        // Relaxed external rows (non-kebab folder names) are reachable only
        // via body_path, which the rebuild cannot re-derive from the display
        // name; carry the caller-verified set verbatim.
        var sql = "SELECT id, name, description, body, origin, created_at, updated_at, body_path, trashed_at FROM skills WHERE trashed_at IS NOT NULL";
        if (carryExternalNames is { Count: > 0 })
        {
            var list = string.Join(",", carryExternalNames.Select(n => "'" + n.Replace("'", "''") + "'"));
            sql += $" OR (origin = 'external' AND trashed_at IS NULL AND name IN ({list}))";
        }

        sql += " ORDER BY name;";

        var rows = new List<TrashCandidate>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(ReadCarryRow(reader));
            }
        }

        var results = new List<TrashRow>(rows.Count);
        foreach (var candidate in rows)
        {
            results.Add(new TrashRow(
                await HydrateAsync(connection, candidate.Row),
                candidate.BodyPath,
                candidate.TrashedAt));
        }

        return results;
    }

    private readonly record struct SkillRow(
        string Id,
        string Name,
        string Description,
        string Body,
        string Origin,
        string CreatedAt,
        string UpdatedAt);

    private readonly record struct ExistingRow(
        long RowId,
        string Id,
        string Name,
        string Description,
        string Body,
        string CreatedAt,
        string? TrashedAt);
}
