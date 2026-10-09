using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Projects;

/// <summary>
/// SQLite store for <see cref="IWorkspacesRegistry"/>: one row per workspace
/// folder Eling has ever registered.
/// </summary>
/// <remarks>
/// Grain is the folder, not the process. That is what makes a closed workspace
/// still findable: the row outlives whatever process wrote it, which is the
/// whole reason this store exists.
/// <para>
/// No ALTER TABLE path and no shape detection: the schema is pre-release, so a
/// database written by an older build is repaired by deleting the file and
/// letting <see cref="EnsureCreatedAsync"/> rebuild it. Rows are cheap to
/// regenerate — a registration re-discovers the folder.
/// </para>
/// </remarks>
public sealed class SqliteWorkspacesRegistry : IWorkspacesRegistry, IDisposable
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS workspaces(
          workspace_id TEXT PRIMARY KEY,
          workspace_root TEXT NOT NULL,
          head_scope_root TEXT NOT NULL,
          codebase_enabled INTEGER NOT NULL,
          first_seen_at TEXT NOT NULL,
          last_seen_at TEXT NOT NULL
        );
        """;

    /// <summary>
    /// One row per folder. This is the upsert's conflict target and the lookup
    /// key; the ULID above only names the row. It is what stops a
    /// re-registration from minting a second id for a workspace that already has
    /// one.
    /// </summary>
    private const string CreateRootIdxSql =
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_workspaces_root ON workspaces(workspace_root);";

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteWorkspacesRegistry>? _logger;

    public SqliteWorkspacesRegistry(
        string dbPath,
        ILogger<SqliteWorkspacesRegistry>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = Path.GetFullPath(dbPath);
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = CreateSql;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var rootIndex = connection.CreateCommand())
        {
            rootIndex.CommandText = CreateRootIdxSql;
            await rootIndex.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records a workspace. The conflict target is the primary key, and
    /// <c>first_seen_at</c> is deliberately absent from the DO UPDATE set — a
    /// folder's first-seen date is history, not something a re-registration
    /// rewrites.
    /// </summary>
    public void Record(RegisteredWorkspace workspace)
    {
        try
        {
            RecordAsync(workspace).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Recording must never be the reason a runtime fails to start.
            // Losing a row costs the dashboard a listing until it registers
            // again; refusing to boot costs everything.
            _logger?.LogWarning(ex, "Failed to record workspace {Root}.", workspace.WorkspaceRoot);
        }
    }

    private async Task RecordAsync(RegisteredWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace.WorkspaceRoot)) return;
        if (string.Equals(workspace.WorkspaceRoot, "UserScope", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(workspace.HeadScopeRoot, "UserScope", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Path.IsPathFullyQualified(workspace.WorkspaceRoot))
        {
            return;
        }

        await EnsureCreatedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workspaces(
              workspace_id, workspace_root, head_scope_root, codebase_enabled,
              first_seen_at, last_seen_at)
            VALUES($id, $root, $scope, $enabled, $first, $last)
            ON CONFLICT(workspace_root) DO UPDATE SET
              head_scope_root=excluded.head_scope_root,
              codebase_enabled=excluded.codebase_enabled,
              last_seen_at=excluded.last_seen_at;
            """;
        // Minted only when the caller has none. The conflict target keeps the id
        // it wrote first — workspace_id is deliberately absent from the DO UPDATE
        // set — so a re-registration refreshes the row instead of duplicating it.
        Add(cmd, "$id", string.IsNullOrWhiteSpace(workspace.WorkspaceId)
            ? System.Ulid.NewUlid().ToString().ToLowerInvariant()
            : workspace.WorkspaceId);
        Add(cmd, "$root", Trimmed(workspace.WorkspaceRoot));
        Add(cmd, "$scope", Trimmed(workspace.HeadScopeRoot));
        Add(cmd, "$enabled", workspace.CodebaseEnabled ? 1 : 0);
        Add(cmd, "$first", Format(workspace.FirstSeenAt));
        Add(cmd, "$last", Format(workspace.LastSeenAt));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RegisteredWorkspace>> ListAsync(CancellationToken cancellationToken = default)
    {
        // A missing file is an empty registry, not an error: nothing registered yet.
        if (!File.Exists(_dbPath)) return Array.Empty<RegisteredWorkspace>();

        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT workspace_id, workspace_root, head_scope_root, codebase_enabled,
                       first_seen_at, last_seen_at
                FROM workspaces ORDER BY last_seen_at DESC, workspace_id DESC;
                """;
            var rows = new List<RegisteredWorkspace>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new RegisteredWorkspace(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    Parse(reader.GetString(4)),
                    Parse(reader.GetString(5))));
            }
            return rows;
        }
        catch (SqliteException ex) when (IsMissingSchema(ex))
        {
            return Array.Empty<RegisteredWorkspace>();
        }
    }

    public async Task<RegisteredWorkspace?> FindByRootAsync(string root, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        if (!File.Exists(_dbPath)) return null;

        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT workspace_id, workspace_root, head_scope_root, codebase_enabled,
                       first_seen_at, last_seen_at
                FROM workspaces WHERE workspace_root=$root LIMIT 1;
                """;
            Add(cmd, "$root", Trimmed(root));
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new RegisteredWorkspace(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                Parse(reader.GetString(4)),
                Parse(reader.GetString(5)));
        }
        catch (SqliteException ex) when (IsMissingSchema(ex))
        {
            return null;
        }
    }

    public async Task<bool> DeleteByRootAsync(string root, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        if (!File.Exists(_dbPath)) return false;

        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM workspaces WHERE workspace_root=$root;";
            Add(cmd, "$root", Trimmed(root));
            var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            return affected > 0;
        }
        catch (SqliteException ex) when (IsMissingSchema(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// True only for "no such table". A locked (5) or corrupt (11) database is a
    /// different fault and must still surface.
    /// </summary>
    private static bool IsMissingSchema(SqliteException ex) => ex.SqliteErrorCode == 1;

    private static string Trimmed(string root)
        => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : default;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void Add(DbCommand cmd, string name, object? value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(parameter);
    }

    public void Dispose() { }
}