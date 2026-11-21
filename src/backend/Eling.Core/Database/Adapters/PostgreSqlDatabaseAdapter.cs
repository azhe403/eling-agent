using Microsoft.Extensions.Logging;
using Npgsql;

using Eling.Core.Database;

namespace Eling.Core.Database.Adapters;

public sealed class PostgreSqlDatabaseAdapter : IDatabaseAdapter
{
    private sealed record TableInfo(
        string Name,
        string Kind);

    private sealed record IndexGroup(
        bool Unique,
        List<string> Cols);

    private readonly DatabaseConnection _connection;
    private readonly ILogger<PostgreSqlDatabaseAdapter>? _logger;

    // A connection that names no database is a server-level connection:
    // PostgreSQL refuses to open it (there is no implicit default like
    // MySQL's), so the adapter points at the maintenance database on its
    // private copy of the string. The stored string is never mutated.
    private string ConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(_connection.ConnectionString);
            if (string.IsNullOrWhiteSpace(builder.Database))
            {
                builder.Database = "postgres";
            }
            if (!_connection.ConnectionString.Contains("Timeout=", StringComparison.OrdinalIgnoreCase))
            {
                builder.Timeout = 3;
            }
            return builder.ConnectionString;
        }
    }

    public PostgreSqlDatabaseAdapter(
        DatabaseConnection connection,
        ILogger<PostgreSqlDatabaseAdapter>? logger = null)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            try { await conn.OpenAsync(ct); } catch (Exception ex) { throw new DatabaseUnavailableException(Categorize(ex), ex); }
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
            _logger?.LogWarning(ex, "PostgreSQL connection test failed.");
            return new ConnectionTestResult(false, "connection failed", sw.ElapsedMilliseconds);
        }
    }

    public async Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default, IReadOnlyList<SqlParam>? parameters = null)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        try { await conn.OpenAsync(ct); } catch (Exception ex) { throw new DatabaseUnavailableException(Categorize(ex), ex); }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        if (parameters is not null)
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            }
        }
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
        await using var conn = new NpgsqlConnection(ConnectionString);
        try { await conn.OpenAsync(ct); } catch (Exception ex) { throw new DatabaseUnavailableException(Categorize(ex), ex); }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        try { await conn.OpenAsync(ct); } catch (Exception ex) { throw new DatabaseUnavailableException(Categorize(ex), ex); }
        await using var cmd = conn.CreateCommand();
        // The templates are PostgreSQL's own bookkeeping and are hidden by
        // the server itself, never something a person means when they pick
        // a database to browse. "postgres" stays: it is real data.
        cmd.CommandText = "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname;";
        cmd.CommandTimeout = 30;

        var databases = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            databases.Add(reader.GetString(0));
        }

        return databases;
    }

    public async Task<DatabaseSchemaInfo> GetSchemaAsync(string? database = null, CancellationToken ct = default)
    {
        var tables = new List<TableSchema>();
        await using var conn = new NpgsqlConnection(ConnectionString);
        try { await conn.OpenAsync(ct); } catch (Exception ex) { throw new DatabaseUnavailableException(Categorize(ex), ex); }

        var tableNames = new List<TableInfo>();
        await using (var cmd = conn.CreateCommand())
        {
            // Introspection is scoped to the current schema: the one
            // namespace the connection is pointed at, exactly the MySQL
            // one-database-per-connection rule. Tables in other schemas are
            // invisible until the connection's Search Path says otherwise.
            cmd.CommandText =
                "SELECT table_name, table_type FROM information_schema.tables "
                + "WHERE table_schema = current_schema() ORDER BY table_name;";
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

            var pkColumns = new HashSet<string>(StringComparer.Ordinal);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT kcu.column_name "
                    + "FROM information_schema.table_constraints tc "
                    + "JOIN information_schema.key_column_usage kcu "
                    + "ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema AND tc.table_name = kcu.table_name "
                    + "WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = current_schema() AND tc.table_name = @t;";
                cmd.CommandTimeout = 30;
                cmd.Parameters.AddWithValue("@t", tname);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    pkColumns.Add(reader.GetString(0));
                }
            }

            var columns = new List<ColumnSchema>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT column_name, data_type, is_nullable, character_maximum_length FROM information_schema.columns "
                    + "WHERE table_schema = current_schema() AND table_name = @t ORDER BY ordinal_position;";
                cmd.CommandTimeout = 30;
                cmd.Parameters.AddWithValue("@t", tname);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(0);
                    var dataType = reader.GetString(1);
                    var nullable = reader.GetString(2);
                    // character_maximum_length is only set when the type declares
                    // a length - a bare "character varying" has none, so it stays null.
                    var maxLength = reader.IsDBNull(3) ? (int?)null : (int)reader.GetInt32(3);
                    columns.Add(new ColumnSchema(
                        name,
                        dataType,
                        string.Equals(nullable, "YES", StringComparison.OrdinalIgnoreCase),
                        pkColumns.Contains(name),
                        maxLength));
                }
            }

            var fks = new List<ForeignKeySchema>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT con.conname AS constraint_name, a.attname AS column_name, "
                    + "       ref.relname AS referenced_table_name, ra.attname AS referenced_column_name "
                    + "FROM pg_constraint con "
                    + "JOIN pg_class rel ON rel.oid = con.conrelid "
                    + "JOIN pg_namespace ns ON ns.oid = rel.relnamespace "
                    + "JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS ck(attnum, ord) ON true "
                    + "JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ck.attnum "
                    + "JOIN LATERAL unnest(con.confkey) WITH ORDINALITY AS fk(attnum, ord2) ON ck.ord = fk.ord2 "
                    + "JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = fk.attnum "
                    + "JOIN pg_class ref ON ref.oid = con.confrelid "
                    + "WHERE con.contype = 'f' AND ns.nspname = current_schema() AND rel.relname = @t "
                    + "ORDER BY con.conname, ck.ord;";
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
                cmd.CommandText =
                    "SELECT i.relname AS index_name, ix.indisunique, a.attname AS column_name, o.ord "
                    + "FROM pg_index ix "
                    + "JOIN pg_class t  ON t.oid = ix.indrelid "
                    + "JOIN pg_namespace n ON n.oid = t.relnamespace "
                    + "JOIN pg_class i  ON i.oid = ix.indexrelid "
                    + "JOIN unnest(ix.indkey::int2[]) WITH ORDINALITY AS o(attnum, ord) ON true "
                    + "JOIN pg_attribute a ON a.attrelid = ix.indrelid AND a.attnum = o.attnum "
                    + "WHERE n.nspname = current_schema() AND t.relname = @t "
                    + "ORDER BY i.relname, o.ord;";
                cmd.CommandTimeout = 30;
                cmd.Parameters.AddWithValue("@t", tname);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var iname = reader.GetString(0);
                    var unique = reader.GetBoolean(1);
                    var col = reader.GetString(2);
                    if (!indexGroups.TryGetValue(iname, out var g))
                    {
                        g = new IndexGroup(unique, new List<string>());
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
    /// Categories a failure for the dashboard and the agent: never the
    /// provider's own words, which can name the account.
    /// </summary>
    private static string Categorize(Exception ex)
    {
        if (ex is PostgresException pg)
        {
            // SQLSTATE: 28P01 invalid password, 28000 invalid authorization
            // specification, 3D000 invalid catalog name (database does not exist).
            return pg.SqlState switch
            {
                "28P01" or "28000" => "authentication failed",
                "3D000" => "database not found",
                _ => "database unavailable"
            };
        }

        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException or OperationCanceledException)
            {
                return "connection timed out";
            }

            if (e is System.Net.Sockets.SocketException)
            {
                return "host unreachable";
            }
        }

        return "database unavailable";
    }
}
