using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Eling.Core.Codebase;

public sealed class SqliteCodebaseIndex : ICodebaseIndex, IDisposable
{
    private const string CreateFilesSql = """
        CREATE TABLE IF NOT EXISTS codebase_files(
          path TEXT PRIMARY KEY,
          hash TEXT NOT NULL,
          mtime INTEGER NOT NULL,
          size INTEGER NOT NULL,
          last_indexed_at TEXT NOT NULL
        );
        """;

    private const string CreateChunksSql = """
        CREATE TABLE IF NOT EXISTS codebase_chunks(
          id TEXT PRIMARY KEY,
          file_path TEXT NOT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          content TEXT NOT NULL,
          hash TEXT NOT NULL,
          FOREIGN KEY(file_path) REFERENCES codebase_files(path) ON DELETE CASCADE
        );
        """;

    private const string CreateIdxSql = "CREATE INDEX IF NOT EXISTS idx_chunks_path ON codebase_chunks(file_path);";

    private const string CreateFtsPorterSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS codebase_fts_porter USING fts5(
          id UNINDEXED, content, path, tokenize='porter unicode61 remove_diacritics 1'
        );
        """;

    private const string CreateFtsTrigramSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS codebase_fts_trigram USING fts5(
          id UNINDEXED, content, path, tokenize='trigram'
        );
        """;

    private readonly string _dbPath;
    private readonly string _connectionString;

    public string DbPath => _dbPath;

    /// <summary>
    /// Opens the index at <paramref name="dbPath"/>.
    ///
    /// <paramref name="readOnly"/> must be true for a sibling project's index
    /// during a federated read. That peer belongs to another process, which may
    /// be writing it at the same moment; a read-write handle on a live SQLite
    /// file risks lock contention against the owner's own writes. Read-only
    /// also makes the "siblings are never written" guarantee enforceable by
    /// the driver rather than by convention.
    /// </summary>
    public SqliteCodebaseIndex(string dbPath, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = Path.GetFullPath(dbPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using var c = await OpenAsync(ct);
        foreach (var sql in new[] { CreateFilesSql, CreateChunksSql, CreateIdxSql, CreateFtsPorterSql, CreateFtsTrigramSql })
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(ct);
    }

    public async Task<CodebaseFileRecord?> GetFileAsync(string path, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, hash, mtime, size, last_indexed_at FROM codebase_files WHERE path=$p LIMIT 1;";
        Add(cmd, "$p", path);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new CodebaseFileRecord(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4));
    }

    public async Task ReplaceChunksAsync(string path, string hash, long mtime, long size, IReadOnlyList<CodebaseChunk> chunks, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);

        // sync codebase_files
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO codebase_files(path, hash, mtime, size, last_indexed_at)
                VALUES($p,$h,$m,$s,$t)
                ON CONFLICT(path) DO UPDATE SET hash=excluded.hash, mtime=excluded.mtime, size=excluded.size, last_indexed_at=excluded.last_indexed_at;
                """;
            Add(cmd, "$p", path); Add(cmd, "$h", hash); Add(cmd, "$m", mtime); Add(cmd, "$s", size); Add(cmd, "$t", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // delete old chunks + FTS rows for this path
        var oldIds = new List<string>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM codebase_chunks WHERE file_path=$p;";
            Add(cmd, "$p", path);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) oldIds.Add(r.GetString(0));
        }
        foreach (var id in oldIds)
        {
            foreach (var tbl in new[] { "codebase_fts_porter", "codebase_fts_trigram" })
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {tbl} WHERE id=$id;";
                Add(cmd, "$id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM codebase_chunks WHERE file_path=$p;";
            Add(cmd, "$p", path);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // insert chunks + FTS
        foreach (var ch in chunks)
        {
            var id = Guid.NewGuid().ToString("N");
            await using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO codebase_chunks(id,file_path,start_line,end_line,content,hash) VALUES($id,$fp,$sl,$el,$ct,$h);";
                Add(cmd, "$id", id); Add(cmd, "$fp", ch.FilePath); Add(cmd, "$sl", ch.StartLine); Add(cmd, "$el", ch.EndLine); Add(cmd, "$ct", ch.Content); Add(cmd, "$h", hash);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var tbl in new[] { "codebase_fts_porter", "codebase_fts_trigram" })
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO {tbl}(id, content, path) VALUES($id,$ct,$p);";
                Add(cmd, "$id", id); Add(cmd, "$ct", ch.Content); Add(cmd, "$p", path);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        var ids = new List<string>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM codebase_chunks WHERE file_path=$p;";
            Add(cmd, "$p", path);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
        }
        foreach (var id in ids)
            foreach (var tbl in new[] { "codebase_fts_porter", "codebase_fts_trigram" })
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {tbl} WHERE id=$id;";
                Add(cmd, "$id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        foreach (var sql in new[] { "DELETE FROM codebase_chunks WHERE file_path=$p;", "DELETE FROM codebase_files WHERE path=$p;" })
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            Add(cmd, "$p", path);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CodebaseHit>> SearchPorterAsync(string ftsQuery, int limit, string? pathPrefix = null, string? filePattern = null, CancellationToken ct = default)
        => await SearchFtsAsync("codebase_fts_porter", ftsQuery, limit, pathPrefix, filePattern, ct);

    public async Task<IReadOnlyList<CodebaseHit>> SearchTrigramAsync(string ftsQuery, int limit, string? pathPrefix = null, string? filePattern = null, CancellationToken ct = default)
        => await SearchFtsAsync("codebase_fts_trigram", ftsQuery, limit, pathPrefix, filePattern, ct);

    public async Task<CodebaseStats> GetStatsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_dbPath)) return new CodebaseStats(0, 0, null, _dbPath);
        await using var c = await OpenAsync(ct);
        int fc = 0, cc = 0; string? last = null;
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT COUNT(*) FROM codebase_files;"; fc = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)); }
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT COUNT(*) FROM codebase_chunks;"; cc = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)); }
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT MAX(last_indexed_at) FROM codebase_files;"; var v = await cmd.ExecuteScalarAsync(ct); last = v as string; }
        return new CodebaseStats(fc, cc, last, _dbPath);
    }

    public async Task<IReadOnlyList<string>> ListPathsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_dbPath)) return Array.Empty<string>();
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path FROM codebase_files;";
        var paths = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) paths.Add(r.GetString(0));
        return paths;
    }

    public async Task<IReadOnlyList<CodebaseFileEntry>> ListRecentFilesAsync(int limit, CancellationToken ct = default)
    {
        if (!File.Exists(_dbPath)) return Array.Empty<CodebaseFileEntry>();
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT f.path, COUNT(c.id), f.last_indexed_at
            FROM codebase_files f LEFT JOIN codebase_chunks c ON c.file_path = f.path
            GROUP BY f.path ORDER BY f.last_indexed_at DESC LIMIT $lim;
            """;
        Add(cmd, "$lim", Math.Min(Math.Max(limit, 1), 100));
        var rows = new List<CodebaseFileEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(new CodebaseFileEntry(r.GetString(0), r.GetInt32(1), r.GetString(2)));
        return rows;
    }

    /// <summary>
    /// Runs one FTS pass. <paramref name="pathPrefix"/> and
    /// <paramref name="filePattern"/> are applied in SQL, inside the same
    /// statement as the rank sort, so the LIMIT is taken from the filtered set
    /// rather than from a larger set that is trimmed afterwards.
    /// </summary>
    private async Task<IReadOnlyList<CodebaseHit>> SearchFtsAsync(string table, string ftsQuery, int limit, string? pathPrefix, string? filePattern, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ftsQuery)) return Array.Empty<CodebaseHit>();
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();

        var filters = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(pathPrefix))
        {
            filters.Add("c.file_path LIKE $pf ESCAPE '\\'");
            Add(cmd, "$pf", EscapeLike(pathPrefix.Trim()) + "%");
        }

        var fileSql = BuildFilePatternSql(filePattern);
        if (fileSql is not null)
        {
            filters.Add("c.file_path LIKE $fp ESCAPE '\\'");
            Add(cmd, "$fp", fileSql);
        }

        var where = filters.Count == 0 ? "" : " AND " + string.Join(" AND ", filters);
        cmd.CommandText = $"""
            SELECT c.file_path, c.start_line, c.end_line, c.content, bm25({table}) AS rank
            FROM {table} f JOIN codebase_chunks c ON c.id=f.id
            WHERE {table} MATCH $q{where}
            ORDER BY rank LIMIT $lim;
            """;
        Add(cmd, "$q", ftsQuery);
        Add(cmd, "$lim", limit);
        var hits = new List<CodebaseHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var score = r.IsDBNull(4) ? 0 : -r.GetDouble(4);
            hits.Add(new CodebaseHit(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), score));
        }
        return hits;
    }

    /// <summary>
    /// Translates the tool's glob-ish file filter into a SQL LIKE pattern.
    /// <c>*.cs</c> becomes a suffix match, a bare fragment becomes a substring
    /// match, and <c>*</c>/<c>*.*</c> mean "no filter". LIKE wildcards inside
    /// the fragment are escaped so a <c>%</c> or <c>_</c> that is genuinely
    /// part of a filename matches literally instead of widening the filter.
    /// </summary>
    private static string? BuildFilePatternSql(string? filePattern)
    {
        if (string.IsNullOrWhiteSpace(filePattern)) return null;
        var pattern = filePattern.Trim();
        if (pattern is "*" or "*.*") return null;
        return pattern.StartsWith('*')
            ? "%" + EscapeLike(pattern[1..])
            : "%" + EscapeLike(pattern) + "%";
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    private static void Add(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    public void Dispose() { }
}
