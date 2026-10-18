using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using Eling.Core.Database;

namespace Eling.Core.Database.Adapters;

public sealed class SqliteDatabaseAdapter : IDatabaseAdapter
{
    private sealed record TableInfo(
        string Name,
        string Kind);

    private sealed record IndexInfo(
        string Name,
        bool Unique);

    private readonly DatabaseConnection _connection;
    private readonly ILogger<SqliteDatabaseAdapter>? _logger;

    public SqliteDatabaseAdapter(
        DatabaseConnection connection,
        ILogger<SqliteDatabaseAdapter>? logger = null)
    {
        _connection = connection;
        _logger = logger;
    }

    private string ResolveConnectionString()
    {
        var builder = new SqliteConnectionStringBuilder(_connection.ConnectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            builder.Pooling = false;
            return builder.ToString();
        }

        if (!Path.IsPathFullyQualified(dataSource))
        {
            dataSource = Path.GetFullPath(dataSource);
        }

        builder.DataSource = dataSource;
        builder.Pooling = false;
        return builder.ToString();
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = new SqliteConnection(ResolveConnectionString());
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
            _logger?.LogWarning(ex, "SQLite connection test failed.");
            return new ConnectionTestResult(false, "connection failed", sw.ElapsedMilliseconds);
        }
    }

    public async Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ResolveConnectionString());
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

        var rows = new List<IReadOnlyList<object?>>();
        var limit = maxRows < 1 ? 1 : maxRows;
        var fetched = 0;
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (fetched >= limit)
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
            fetched++;
        }

        return new QueryResult(columns, rows, truncated);
    }

    public async Task<DatabaseSchemaInfo> GetSchemaAsync(CancellationToken ct = default)
    {
        var tables = new List<TableSchema>();
        await using var conn = new SqliteConnection(ResolveConnectionString());
        await conn.OpenAsync(ct);

        var tableNames = new List<TableInfo>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                tableNames.Add(new TableInfo(reader.GetString(0), reader.GetString(1) == "view" ? "view" : "table"));
            }
        }

        foreach (var tableInfo in tableNames)
        {
            var tname = tableInfo.Name;
            var kind = tableInfo.Kind;
            var columns = new List<ColumnSchema>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + (char)34 + tname.Replace(((char)34).ToString(), ((char)34).ToString() + ((char)34).ToString()) + (char)34 + ");";
                cmd.CommandTimeout = 30;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(1);
                    var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var notnull = reader.GetInt32(3);
                    var pk = reader.GetInt32(5);
                    columns.Add(new ColumnSchema(name, type, notnull == 0, pk > 0));
                }
            }

            var fks = new List<ForeignKeySchema>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_key_list(" + (char)34 + tname.Replace(((char)34).ToString(), ((char)34).ToString() + ((char)34).ToString()) + (char)34 + ");";
                cmd.CommandTimeout = 30;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var fkTable = reader.GetString(2);
                    var fromCol = reader.GetString(3);
                    var toCol = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    var fkName = $"fk_{tname}_{fromCol}";
                    fks.Add(new ForeignKeySchema(fkName, fromCol, fkTable, toCol));
                }
            }

            var indexes = new List<IndexSchema>();
            var indexList = new List<IndexInfo>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA index_list(" + (char)34 + tname.Replace(((char)34).ToString(), ((char)34).ToString() + ((char)34).ToString()) + (char)34 + ");";
                cmd.CommandTimeout = 30;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var iname = reader.GetString(1);
                    var unique = reader.GetInt32(2) == 1;
                    indexList.Add(new IndexInfo(iname, unique));
                }
            }
            foreach (var indexInfo in indexList)
            {
                var iname = indexInfo.Name;
                var unique = indexInfo.Unique;
                var cols = new List<string>();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA index_info(" + (char)34 + iname.Replace(((char)34).ToString(), ((char)34).ToString() + ((char)34).ToString()) + (char)34 + ");";
                    cmd.CommandTimeout = 30;
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        cols.Add(reader.GetString(2));
                    }
                }
                indexes.Add(new IndexSchema(iname, unique, cols));
            }

            tables.Add(new TableSchema(tname, kind, columns, fks, indexes));
        }

        return new DatabaseSchemaInfo(tables);
    }
}