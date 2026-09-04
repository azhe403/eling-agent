using Eling.Core;
using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace Eling.Core;

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

    private const string CreateSearchSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
            id UNINDEXED,
            content,
            tags,
            source
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

    private readonly string _connectionString;

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

        await DeleteSearchRowAsync(connection, transaction, memory.Id);
        await InsertSearchRowAsync(connection, transaction, memory);
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

        await DeleteSearchRowAsync(connection, transaction, id);
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

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM memory_fts;";
            await command.ExecuteNonQueryAsync();
        }

        foreach (var memory in items)
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = UpsertMemorySql;
                AddParameters(command, memory);
                await command.ExecuteNonQueryAsync();
            }

            await InsertSearchRowAsync(connection, transaction, memory);
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        // Free text must not be fed to MATCH as FTS5 query language: reserved
        // syntax (e.g. "<term>:" column filters, "-", unbalanced quotes) then
        // throws "no such column: <term>" / syntax errors. Quote each
        // whitespace-separated token as a phrase so input is searched literally.
        var ftsQuery = BuildFtsQuery(query);
        if (ftsQuery.Length == 0)
        {
            // Only punctuation/operators survived sanitization - nothing to match.
            return Array.Empty<MemorySearchResult>();
        }

        await using var connection = await OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, bm25(memory_fts) AS rank
            FROM memory_fts
            WHERE memory_fts MATCH $query
            ORDER BY rank;
            """;
        AddParameter(command, "$query", ftsQuery);

        var results = new List<MemorySearchResult>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new MemorySearchResult(
                new MemoryId(reader.GetString(0)),
                reader.GetDouble(1)));
        }

        return results;
    }

    private static string BuildFtsQuery(string query)
    {
        var phrases = new List<string>();
        foreach (var rawToken in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // A literal double quote would terminate an FTS5 phrase; drop it.
            var token = rawToken.Replace("\"", string.Empty);
            if (!token.Any(char.IsLetterOrDigit))
            {
                continue;
            }
            phrases.Add($"\"{token}\"");
        }
        return string.Join(' ', phrases);
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await EnsureSchemaAsync(connection);
        return connection;
    }

    private static async Task EnsureSchemaAsync(DbConnection connection)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = CreateMemoriesSql;
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = CreateSearchSql;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task DeleteSearchRowAsync(DbConnection connection, DbTransaction transaction, MemoryId id)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM memory_fts WHERE id = $id;";
        AddParameter(command, "$id", id.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertSearchRowAsync(DbConnection connection, DbTransaction transaction, Memory memory)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO memory_fts (id, content, tags, source)
            VALUES ($id, $content, $tags, $source);
            """;
        AddParameter(command, "$id", memory.Id.Value);
        AddParameter(command, "$content", memory.Content);
        AddParameter(command, "$tags", string.Join(",", memory.Tags));
        AddParameter(command, "$source", memory.Source);
        await command.ExecuteNonQueryAsync();
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
