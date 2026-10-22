using Microsoft.Extensions.Logging;
using MySqlConnector;

using Eling.Core.Database;

namespace Eling.Core.Database.Adapters;

public sealed class MySqlDatabaseAdapter : IDatabaseAdapter
{
    private sealed record TableInfo(
        string Name,
        string Kind);

    private sealed record IndexGroup(
        bool Unique,
        List<string> Cols);

    private readonly DatabaseConnection _connection;
    private readonly ILogger<MySqlDatabaseAdapter>? _logger;

    // MySqlConnector maps CHAR(36) columns to System.Guid by default and throws
    // when the stored value is not a valid GUID. Read them as strings instead so
    // arbitrary CHAR(36) data (e.g. "A") does not break queries/schema reads.
    private string ConnectionString
    {
        get
        {
            var builder = new MySqlConnectionStringBuilder(_connection.ConnectionString);
            if (!builder.ConnectionString.Contains("GuidFormat", StringComparison.OrdinalIgnoreCase))
            {
                builder.GuidFormat = MySqlGuidFormat.None;
            }
            return builder.ConnectionString;
        }
    }

    public MySqlDatabaseAdapter(
        DatabaseConnection connection,
        ILogger<MySqlDatabaseAdapter>? logger = null)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = new MySqlConnection(ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            cmd.CommandTimeout = 30;
            await cmd.ExecuteScalarAsync(ct);
            sw.Stop();
            return new ConnectionTestResult(true, "ok", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogWarning(ex, "MySQL connection test failed.");
            return new ConnectionTestResult(false, "connection failed", sw.ElapsedMilliseconds);
        }
    }

    public async Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        var limit = maxRows < 1 ? 1 : maxRows;
        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= limit)
            {
                truncated = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var v = reader.GetValue(i);
                row[i] = v is DBNull ? null : v;
            }
            rows.Add(row);
        }

        return new QueryResult(columns, rows, truncated);
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // SHOW DATABASES lists what the account may see, and the system
        // schemas are noise in a picker.
        cmd.CommandText = "SHOW DATABASES;";
        cmd.CommandTimeout = 30;

        var databases = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (IsSystemDatabase(name)) continue;
            databases.Add(name);
        }

        return databases;
    }

    /// <summary>
    /// MySQL's own bookkeeping schemas - never something a person means when
    /// they pick a database to browse.
    /// </summary>
    private static bool IsSystemDatabase(string name) =>
        name.Equals("information_schema", StringComparison.OrdinalIgnoreCase)
        || name.Equals("performance_schema", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mysql", StringComparison.OrdinalIgnoreCase)
        || name.Equals("sys", StringComparison.OrdinalIgnoreCase);

    public async Task<DatabaseSchemaInfo> GetSchemaAsync(string? database = null, CancellationToken ct = default)
    {
        var tables = new List<TableSchema>();
        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        // A connection without a default database is a server-level
        // connection: it reaches every database, and the tree lists them
        // instead of the tables of one of them.
        var schemaFilter = string.IsNullOrWhiteSpace(database)
            ? "table_schema = DATABASE()"
            : "table_schema = @schema";

        var tableNames = new List<TableInfo>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT table_name, table_type FROM information_schema.tables WHERE {schemaFilter} ORDER BY table_name;";
            cmd.CommandTimeout = 30;
            AddSchemaParameter(cmd, database);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1);
                var kind = type.Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? "view" : "table";
                tableNames.Add(new TableInfo(name, kind));
            }
        }

        foreach (var tableInfo in tableNames)
        {
            var tname = tableInfo.Name;
            var kind = tableInfo.Kind;
            var columns = new List<ColumnSchema>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT column_name, data_type, is_nullable, column_key FROM information_schema.columns WHERE {schemaFilter} AND table_name = @t ORDER BY ordinal_position;";
                cmd.CommandTimeout = 30;
                AddSchemaParameter(cmd, database);
                cmd.Parameters.AddWithValue("@t", tname);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(0);
                    var dataType = reader.GetString(1);
                    var nullable = reader.GetString(2);
                    var key = reader.GetString(3);
                    columns.Add(new ColumnSchema(name, dataType, string.Equals(nullable, "YES", StringComparison.OrdinalIgnoreCase), string.Equals(key, "PRI", StringComparison.OrdinalIgnoreCase)));
                }
            }

            var fks = new List<ForeignKeySchema>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT constraint_name, column_name, referenced_table_name, referenced_column_name FROM information_schema.key_column_usage WHERE {schemaFilter} AND table_name = @t AND referenced_table_name IS NOT NULL;";
                cmd.CommandTimeout = 30;
                AddSchemaParameter(cmd, database);
                cmd.Parameters.AddWithValue("@t", tname);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    fks.Add(new ForeignKeySchema(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)));
                }
            }

            var indexes = new List<IndexSchema>();
            var indexGroups = new Dictionary<string, IndexGroup>(StringComparer.Ordinal);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT index_name, non_unique, column_name FROM information_schema.statistics WHERE {schemaFilter} AND table_name = @t ORDER BY index_name, seq_in_index;";
                cmd.CommandTimeout = 30;
                AddSchemaParameter(cmd, database);
                cmd.Parameters.AddWithValue("@t", tname);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var iname = reader.GetString(0);
                    var nonUnique = reader.GetInt32(1);
                    var col = reader.GetString(2);
                    if (!indexGroups.TryGetValue(iname, out var g))
                    {
                        g = new IndexGroup(nonUnique == 0, new List<string>());
                        indexGroups[iname] = g;
                    }
                    g.Cols.Add(col);
                }
            }
            foreach (var kv in indexGroups)
            {
                indexes.Add(new IndexSchema(kv.Key, kv.Value.Unique, kv.Value.Cols));
            }

            tables.Add(new TableSchema(tname, kind, columns, fks, indexes));
        }

        return new DatabaseSchemaInfo(tables);
    }

    /// <summary>
    /// Binds the schema the query filters on, when the caller named one. The
    /// filter itself is a fixed string - never caller input - so only the
    /// value is parameterised.
    /// </summary>
    private static void AddSchemaParameter(MySqlCommand cmd, string? database)
    {
        if (!string.IsNullOrWhiteSpace(database))
        {
            cmd.Parameters.AddWithValue("@schema", database);
        }
    }
}
