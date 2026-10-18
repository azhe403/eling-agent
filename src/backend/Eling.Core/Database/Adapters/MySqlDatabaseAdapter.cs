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
            await using var conn = new MySqlConnection(_connection.ConnectionString);
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
        await using var conn = new MySqlConnection(_connection.ConnectionString);
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

    public async Task<DatabaseSchemaInfo> GetSchemaAsync(CancellationToken ct = default)
    {
        var tables = new List<TableSchema>();
        await using var conn = new MySqlConnection(_connection.ConnectionString);
        await conn.OpenAsync(ct);

        var tableNames = new List<TableInfo>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY table_name;";
            cmd.CommandTimeout = 30;
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
                cmd.CommandText = "SELECT column_name, data_type, is_nullable, column_key FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t ORDER BY ordinal_position;";
                cmd.CommandTimeout = 30;
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
                cmd.CommandText = "SELECT constraint_name, column_name, referenced_table_name, referenced_column_name FROM information_schema.key_column_usage WHERE table_schema = DATABASE() AND table_name = @t AND referenced_table_name IS NOT NULL;";
                cmd.CommandTimeout = 30;
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
                cmd.CommandText = "SELECT index_name, non_unique, column_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = @t ORDER BY index_name, seq_in_index;";
                cmd.CommandTimeout = 30;
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
}
