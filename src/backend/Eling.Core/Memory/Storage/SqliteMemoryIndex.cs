using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Eling.Core.Memory.Storage;

public sealed class SqliteMemoryIndex : IMemoryIndex
{
    private const string CreateMemoriesSql = """
        CREATE TABLE IF NOT EXISTS memories (
            id TEXT PRIMARY KEY,
            type TEXT NOT NULL,
            status TEXT NOT NULL,
            content TEXT NOT NULL,
            tags TEXT,
            source TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    // Layer 1: Porter stemmer - root-form matching (hygien* matches hygiene/hygienic/hygien)
    private const string CreateSearchPorterSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts_porter USING fts5(
            id UNINDEXED,
            content,
            tags,
            source,
            tokenize='porter unicode61 remove_diacritics 1'
        );
        """;

    // Layer 2: Trigram - substring/typo-tolerant matching (hygene matches hygiene)
    private const string CreateSearchTrigramSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts_trigram USING fts5(
            id UNINDEXED,
            content,
            tags,
            source,
            tokenize='trigram'
        );
        """;

    private const string UpsertMemorySql = """
        INSERT INTO memories (id, type, status, content, tags, source, created_at, updated_at)
        VALUES ($id, $type, $status, $content, $tags, $source, $createdAt, $updatedAt)
        ON CONFLICT(id) DO UPDATE SET
            type = excluded.type,
            status = excluded.status,
            content = excluded.content,
            tags = excluded.tags,
            source = excluded.source,
            updated_at = excluded.updated_at;
        """;

    private const string DropLegacySearchSql = "DROP TABLE IF EXISTS memory_fts;";

    private const string DropPorterSearchSql = "DROP TABLE IF EXISTS memory_fts_porter;";
    private const string DropTrigramSearchSql = "DROP TABLE IF EXISTS memory_fts_trigram;";

    private const string DetectLegacySchemaSql = """
        SELECT COUNT(*) FROM sqlite_master
        WHERE type='table' AND name='memory_fts';
        """;

    private const int AndToOrThreshold = 3;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _ftsReady;

    public SqliteMemoryIndex(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
    }

    public async Task IndexAsync(Memory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        await using var connection = await OpenConnectionAsync();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = UpsertMemorySql;
            AddParameters(command, memory);
            await command.ExecuteNonQueryAsync();
        }

        await DeleteSearchRowsAsync(connection, transaction, memory.Id);
        await InsertSearchRowsAsync(connection, transaction, memory);
        await transaction.CommitAsync();
    }

    public async Task RemoveAsync(MemoryId id)
    {
        await using var connection = await OpenConnectionAsync();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM memories WHERE id = $id;";
            AddParameter(command, "$id", id.Value);
            await command.ExecuteNonQueryAsync();
        }

        await DeleteSearchRowsAsync(connection, transaction, id);
        await transaction.CommitAsync();
    }

    public async Task RebuildAsync(IEnumerable<Memory> memories)
    {
        ArgumentNullException.ThrowIfNull(memories);
        var items = memories.ToList();

        await using var connection = await OpenConnectionAsync();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM memories;";
            await command.ExecuteNonQueryAsync();
        }

        await ClearFtsTablesAsync(connection, transaction);

        foreach (var memory in items)
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = UpsertMemorySql;
                AddParameters(command, memory);
                await command.ExecuteNonQueryAsync();
            }

            await InsertSearchRowsAsync(connection, transaction, memory);
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var tokens = ExtractTokens(query);
        if (tokens.Length == 0)
        {
            return Array.Empty<MemorySearchResult>();
        }

        // Phase 1: AND on porter + trigram. Default precision.
        var porterAndQuery = BuildPorterAndQuery(tokens);
        var trigramAndQuery = BuildTrigramAndQuery(tokens);

        var porterAnd = porterAndQuery.Length > 0
            ? await SearchPorterAsync(porterAndQuery)
            : Array.Empty<(string Id, double Score)>();
        var trigramAnd = trigramAndQuery.Length > 0
            ? await SearchTrigramAsync(trigramAndQuery)
            : Array.Empty<(string Id, double Score)>();

        var andMode = MergeRankings(porterAnd, trigramAnd, "and");
        if (andMode.Count >= AndToOrThreshold)
        {
            return andMode;
        }

        // Phase 2: OR fallback. Necessary because AND can return 0 results when the
        // topic list is broad and no memory contains every term; BM25 still ranks
        // the OR results so the most cross-relevant memories lead.
        var porterOrQuery = BuildPorterOrQuery(tokens);
        var trigramOrQuery = BuildTrigramOrQuery(tokens);

        var porterOr = porterOrQuery.Length > 0
            ? await SearchPorterAsync(porterOrQuery)
            : Array.Empty<(string Id, double Score)>();
        var trigramOr = trigramOrQuery.Length > 0
            ? await SearchTrigramAsync(trigramOrQuery)
            : Array.Empty<(string Id, double Score)>();

        return MergeRankings(porterOr, trigramOr, "or-fallback");
    }

    private static string[] ExtractTokens(string query)
    {
        return query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", string.Empty).Trim().ToLowerInvariant())
            .Where(t => t.Length > 0 && t.Any(char.IsLetterOrDigit))
            .ToArray();
    }

    private static string BuildPorterAndQuery(string[] tokens)
    {
        var phrases = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Length >= 2)
            {
                phrases.Add($"\"{token}\"");
            }
        }
        return string.Join(' ', phrases);
    }

    private static string BuildPorterOrQuery(string[] tokens)
    {
        var phrases = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Length >= 2)
            {
                phrases.Add($"\"{token}\"");
            }
        }
        return string.Join(" OR ", phrases);
    }

    private static string BuildTrigramAndQuery(string[] tokens)
    {
        // Trigram tokenizer: each token becomes a substring to search.
        // Short tokens (< 3 chars) cannot form trigrams and are skipped.
        var phrases = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Length >= 3)
            {
                phrases.Add($"\"{token}\"");
            }
        }
        return string.Join(' ', phrases);
    }

    private static string BuildTrigramOrQuery(string[] tokens)
    {
        var phrases = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Length >= 3)
            {
                phrases.Add($"\"{token}\"");
            }
        }
        return string.Join(" OR ", phrases);
    }

    private static IReadOnlyCollection<MemorySearchResult> MergeRankings(
        IReadOnlyCollection<(string Id, double Score)> porter,
        IReadOnlyCollection<(string Id, double Score)> trigram,
        string queryMode)
    {
        // Porter is primary signal (semantic match); trigram is secondary (typo/substring).
        // Weight: porter x 1.0, trigram x 0.4. Deduplicate by id, sum scores, sort descending.
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        var porterById = new Dictionary<string, double>(StringComparer.Ordinal);
        var trigramById = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (id, score) in porter)
        {
            // BM25 returns negative ranks; more-negative = better. Invert so higher = better.
            var positive = -score;
            porterById[id] = positive;
            totals[id] = totals.GetValueOrDefault(id) + positive;
        }
        foreach (var (id, score) in trigram)
        {
            var weighted = (-score) * 0.4;
            trigramById[id] = trigramById.GetValueOrDefault(id) + weighted;
            totals[id] = totals.GetValueOrDefault(id) + weighted;
        }

        return totals
            .OrderByDescending(kv => kv.Value)
            .Select(kv =>
            {
                var layers = new List<string>(2);
                if (porterById.ContainsKey(kv.Key)) layers.Add("porter");
                if (trigramById.ContainsKey(kv.Key)) layers.Add("trigram");
                return new MemorySearchResult(
                    new MemoryId(kv.Key),
                    kv.Value,
                    layers,
                    porterById.GetValueOrDefault(kv.Key),
                    trigramById.GetValueOrDefault(kv.Key),
                    queryMode);
            })
            .ToList();
    }

    private async Task<IReadOnlyCollection<(string Id, double Score)>> SearchPorterAsync(string ftsQuery)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, bm25(memory_fts_porter) AS rank
            FROM memory_fts_porter
            WHERE memory_fts_porter MATCH $query
            ORDER BY rank;
            """;
        AddParameter(command, "$query", ftsQuery);
        var results = new List<(string, double)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((reader.GetString(0), reader.GetDouble(1)));
        }
        return results;
    }

    private async Task<IReadOnlyCollection<(string Id, double Score)>> SearchTrigramAsync(string ftsQuery)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, bm25(memory_fts_trigram) AS rank
            FROM memory_fts_trigram
            WHERE memory_fts_trigram MATCH $query
            ORDER BY rank;
            """;
        AddParameter(command, "$query", ftsQuery);
        var results = new List<(string, double)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((reader.GetString(0), reader.GetDouble(1)));
        }
        return results;
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await EnsureInitializedAsync(connection);
        return connection;
    }

    private async Task EnsureInitializedAsync(DbConnection connection)
    {
        if (_ftsReady) return;

        await _initGate.WaitAsync();
        try
        {
            if (_ftsReady) return;

            await EnsureSchemaAsync(connection);
            await RepopulateFtsFromMemoriesTableAsync(connection);

            _ftsReady = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private static async Task EnsureSchemaAsync(DbConnection connection)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = CreateMemoriesSql;
            await command.ExecuteNonQueryAsync();
        }

        // Migration: drop legacy single-table FTS if present (introduced before
        // the 3-layer architecture). The new tables memory_fts_porter and
        // memory_fts_trigram replace it.
        var hasLegacy = await HasLegacySchemaAsync(connection);
        if (hasLegacy)
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = DropLegacySearchSql;
            await drop.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = CreateSearchPorterSql;
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = CreateSearchTrigramSql;
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// If the FTS tables exist but hold no rows while the durable memories
    /// table does, repopulate the FTS layers from it. This happens after the
    /// legacy → 3-layer schema migration (the new FTS tables start empty) and
    /// on any fresh start where the index was previously populated but the FTS
    /// virtual tables were dropped/recreated without a rebuild.
    /// </summary>
    private static async Task RepopulateFtsFromMemoriesTableAsync(DbConnection connection)
    {
        var ftsRowCount = await CountRowsAsync(connection, "memory_fts_porter");
        if (ftsRowCount > 0)
        {
            return;
        }

        var memoryRows = await ReadMemoryRowsAsync(connection);
        if (memoryRows.Count == 0)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        foreach (var table in new[] { "memory_fts_porter", "memory_fts_trigram" })
        {
            foreach (var row in memoryRows)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    INSERT INTO {table} (id, content, tags, source)
                    VALUES ($id, $content, $tags, $source);
                    """;
                AddParameter(command, "$id", row.Id);
                AddParameter(command, "$content", row.Content);
                AddParameter(command, "$tags", row.Tags);
                AddParameter(command, "$source", row.Source);
                await command.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }

    private static async Task<long> CountRowsAsync(DbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        var result = await command.ExecuteScalarAsync();
        return result is long count ? count : 0;
    }

    private static async Task<List<MemoryTableRow>> ReadMemoryRowsAsync(DbConnection connection)
    {
        var rows = new List<MemoryTableRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, content, tags, source
            FROM memories;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new MemoryTableRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return rows;
    }

    private sealed record MemoryTableRow(string Id, string Content, string Tags, string? Source);

    private static async Task<bool> HasLegacySchemaAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = DetectLegacySchemaSql;
        var result = await command.ExecuteScalarAsync();
        return result is long count && count > 0;
    }

    private static async Task ClearFtsTablesAsync(DbConnection connection, DbTransaction transaction)
    {
        foreach (var sql in new[] { "DELETE FROM memory_fts_porter;", "DELETE FROM memory_fts_trigram;" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task DeleteSearchRowsAsync(DbConnection connection, DbTransaction transaction, MemoryId id)
    {
        foreach (var table in new[] { "memory_fts_porter", "memory_fts_trigram" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE id = $id;";
            AddParameter(command, "$id", id.Value);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertSearchRowsAsync(DbConnection connection, DbTransaction transaction, Memory memory)
    {
        var searchFields = BuildSearchFields(memory);
        foreach (var table in new[] { "memory_fts_porter", "memory_fts_trigram" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {table} (id, content, tags, source)
                VALUES ($id, $content, $tags, $source);
                """;
            AddParameter(command, "$id", memory.Id.Value);
            AddParameter(command, "$content", searchFields.Content);
            AddParameter(command, "$tags", searchFields.Tags);
            AddParameter(command, "$source", searchFields.Source);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static (string Content, string Tags, string? Source) BuildSearchFields(Memory memory)
    {
        // Tags are already normalized by Memory.NormalizeTags at construction
        // time; join with single space so FTS5 tokenizes each word independently.
        return (
            Content: memory.Content,
            Tags: string.Join(' ', memory.Tags),
            Source: memory.Source);
    }

    private static void AddParameters(DbCommand command, Memory memory)
    {
        AddParameter(command, "$id", memory.Id.Value);
        AddParameter(command, "$type", memory.Type.ToString());
        AddParameter(command, "$status", memory.Status.ToString());
        AddParameter(command, "$content", memory.Content);
        AddParameter(command, "$tags", string.Join(",", memory.Tags));
        AddParameter(command, "$source", memory.Source);
        AddParameter(command, "$createdAt", memory.CreatedAt.ToString("O"));
        AddParameter(command, "$updatedAt", memory.UpdatedAt.ToString("O"));
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
