using Eling.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Eling.Core.Audit.Storage;

/// <summary>
/// Rebuildable read cache over the JSONL files, normalized into three relational
/// tables (audit_events, audit_state, audit_tags) with no JSON payload column.
/// Losing this database is safe: <see cref="RebuildAsync"/> rescans every JSONL
/// file and repopulates it, the same cache-not-truth contract as the memory index.
/// </summary>
public sealed class SqliteAuditIndex(string auditRoot, AuditFileStore store) : IAuditIndex
{
    private const string After = "after";
    private const string Previous = "previous";

    private string DbPath => Path.Combine(auditRoot, "audit-index.db");

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DbPath,
        Pooling = false,
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(auditRoot);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS audit_events (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp    TEXT NOT NULL,
                trail        TEXT NOT NULL,
                category     TEXT NOT NULL,
                action       TEXT NOT NULL,
                actor        TEXT NOT NULL,
                source       TEXT NOT NULL,
                scope        TEXT NULL,
                project_root TEXT NULL,
                target       TEXT NULL,
                outcome      TEXT NOT NULL,
                duration_ms  INTEGER NULL,
                byte_size    INTEGER NULL,
                hash         TEXT NULL,
                prev_hash    TEXT NULL,
                query_text   TEXT NULL,
                result_count INTEGER NULL,
                path         TEXT NULL,
                matches      INTEGER NULL,
                pid          INTEGER NULL,
                file         TEXT NOT NULL,
                line         INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_events_timestamp ON audit_events(timestamp);
            CREATE INDEX IF NOT EXISTS ix_audit_events_action ON audit_events(action);
            CREATE INDEX IF NOT EXISTS ix_audit_events_actor ON audit_events(actor);

            CREATE TABLE IF NOT EXISTS audit_state (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id      INTEGER NOT NULL REFERENCES audit_events(id) ON DELETE CASCADE,
                side          TEXT NOT NULL,
                content       TEXT NULL,
                status        TEXT NULL,
                memory_source TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_state_event ON audit_state(event_id);

            CREATE TABLE IF NOT EXISTS audit_tags (
                state_id INTEGER NOT NULL REFERENCES audit_state(id) ON DELETE CASCADE,
                ordinal  INTEGER NOT NULL,
                tag      TEXT NOT NULL,
                PRIMARY KEY (state_id, ordinal)
            );
            """, cancellationToken);
    }

    public async Task IndexAsync(IReadOnlyList<AuditRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var record in records)
        {
            var eventId = await InsertEventAsync(connection, transaction, record, cancellationToken);
            await InsertStateAsync(connection, transaction, eventId, After, record.Event.Metadata, cancellationToken);
            await InsertStateAsync(connection, transaction, eventId, Previous, record.Event.PreviousMetadata, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<long> InsertEventAsync(
        SqliteConnection connection, SqliteTransaction transaction, AuditRecord record, CancellationToken cancellationToken)
    {
        var e = record.Event;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events
                (timestamp, trail, category, action, actor, source, scope, project_root, target,
                 outcome, duration_ms, byte_size, hash, prev_hash,
                 query_text, result_count, path, matches, pid, file, line)
            VALUES
                ($ts, $trail, $cat, $action, $actor, $source, $scope, $root, $target,
                 $outcome, $duration, $size, $hash, $prevhash,
                 $query, $count, $path, $matches, $pid, $file, $line);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ts", e.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$trail", AuditLabels.ToWireString(e.Trail));
        command.Parameters.AddWithValue("$cat", AuditLabels.ToWireString(e.Category));
        command.Parameters.AddWithValue("$action", e.Action);
        command.Parameters.AddWithValue("$actor", e.Actor);
        command.Parameters.AddWithValue("$source", e.Source);
        command.Parameters.AddWithValue("$scope", (object?)(e.Scope.HasValue ? AuditLabels.ToWireString(e.Scope.Value) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$root", (object?)e.ProjectRoot ?? DBNull.Value);
        command.Parameters.AddWithValue("$target", (object?)e.Target ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", AuditLabels.ToWireString(e.Outcome));
        command.Parameters.AddWithValue("$duration", (object?)e.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)e.ByteSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)e.Hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$prevhash", (object?)e.PrevHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", (object?)e.Query ?? DBNull.Value);
        command.Parameters.AddWithValue("$count", (object?)e.ResultCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)e.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$matches", (object?)e.Matches ?? DBNull.Value);
        command.Parameters.AddWithValue("$pid", (object?)e.Pid ?? DBNull.Value);
        command.Parameters.AddWithValue("$file", record.File);
        command.Parameters.AddWithValue("$line", record.Line);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, long eventId,
        string side, AuditMetadata? state, CancellationToken cancellationToken)
    {
        if (state is null) return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_state (event_id, side, content, status, memory_source)
            VALUES ($event, $side, $content, $status, $source);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$side", side);
        command.Parameters.AddWithValue("$content", (object?)state.Content ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)state.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)state.MemorySource ?? DBNull.Value);
        var stateId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));

        if (state.Tags is not { Count: > 0 }) return;
        for (var ordinal = 0; ordinal < state.Tags.Count; ordinal++)
        {
            await using var tagCommand = connection.CreateCommand();
            tagCommand.Transaction = transaction;
            tagCommand.CommandText = "INSERT INTO audit_tags (state_id, ordinal, tag) VALUES ($state, $ord, $tag);";
            tagCommand.Parameters.AddWithValue("$state", stateId);
            tagCommand.Parameters.AddWithValue("$ord", ordinal);
            tagCommand.Parameters.AddWithValue("$tag", state.Tags[ordinal]);
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            await ExecuteAsync(connection, "DELETE FROM audit_tags; DELETE FROM audit_state; DELETE FROM audit_events;", cancellationToken);
        }

        var records = new List<AuditRecord>();
        foreach (var trail in new[] { AuditTrail.Change, AuditTrail.Access })
        {
            // The trail directory comes from the store rather than a second
            // hard-coded "changes"/"access" mapping, and every day form is read
            // through the shared day-file reader: a rebuild after rotation must
            // still see the closed days, and Directory.GetFiles(dir, "*.jsonl")
            // does not match "*.jsonl.gz".
            var dir = Path.GetDirectoryName(store.GetCurrentFilePath(trail, DateTimeOffset.UnixEpoch))!;
            await foreach (var day in AuditDayFiles.ReadAsync(dir, trail, cancellationToken))
            {
                var line = 0;
                foreach (var entry in day.Entries)
                {
                    line++;
                    records.Add(new AuditRecord(Path.GetFileName(day.SourceFile), line, entry));
                }
            }
        }

        // Chunk to keep the write transaction bounded.
        foreach (var chunk in records.Chunk(500))
        {
            await IndexAsync(chunk, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var rows = new List<AuditEventRow>();
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, file, line, timestamp, trail, category, action, actor, source, scope,
                       project_root, target, outcome, duration_ms, byte_size, hash, prev_hash,
                       query_text, result_count, path, matches, pid
                FROM audit_events
                WHERE trail = $trail COLLATE NOCASE
                  AND ($action IS NULL OR action = $action)
                  AND ($actor IS NULL OR actor = $actor)
                  AND ($from IS NULL OR timestamp >= $from)
                  AND ($to IS NULL OR timestamp <= $to)
                ORDER BY timestamp DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$trail", AuditLabels.ToWireString(query.Trail));
            // Case-insensitive on purpose: an upgraded database still holds rows
            // written by the previous PascalCase writer ("Change"), and SQLite's
            // TEXT "=" is case-sensitive — without this those rows are silently
            // invisible to every query.
            command.Parameters.AddWithValue("$action", (object?)query.Action ?? DBNull.Value);
            command.Parameters.AddWithValue("$actor", (object?)query.Actor ?? DBNull.Value);
            command.Parameters.AddWithValue("$from", (object?)query.From?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$to", (object?)query.To?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", query.Limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new AuditEventRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    new AuditEvent
                    {
                        Timestamp = DateTimeOffset.Parse(reader.GetString(3)),
                        Trail = ParseLabel<AuditTrail>(reader.GetString(4)),
                        Category = ParseLabel<AuditCategory>(reader.GetString(5)),
                        Action = reader.GetString(6),
                        Actor = reader.GetString(7),
                        Source = reader.GetString(8),
                        Scope = reader.IsDBNull(9)
                            ? null
                            : ParseScope(reader.GetString(9)),
                        ProjectRoot = reader.IsDBNull(10) ? null : reader.GetString(10),
                        Target = reader.IsDBNull(11) ? null : reader.GetString(11),
                        Outcome = ParseLabel<AuditOutcome>(reader.GetString(12)),
                        DurationMs = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                        ByteSize = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                        Hash = reader.IsDBNull(15) ? null : reader.GetString(15),
                        PrevHash = reader.IsDBNull(16) ? null : reader.GetString(16),
                        Query = reader.IsDBNull(17) ? null : reader.GetString(17),
                        ResultCount = reader.IsDBNull(18) ? null : reader.GetInt32(18),
                        Path = reader.IsDBNull(19) ? null : reader.GetString(19),
                        Matches = reader.IsDBNull(20) ? null : reader.GetInt32(20),
                        Pid = reader.IsDBNull(21) ? null : reader.GetInt32(21)
                    }));
            }
        }

        if (rows.Count == 0) return Array.Empty<AuditRecord>();

        var states = await LoadStatesAsync(rows.Select(r => r.Id).ToArray(), cancellationToken);

        var results = new List<AuditRecord>(rows.Count);
        foreach (var row in rows)
        {
            states.TryGetValue(row.Id, out var pair);
            var state = pair ?? new AuditStatePair(null, null);
            results.Add(new AuditRecord(row.File, row.Line, WithState(row.Event, state.After, state.Previous)));
        }
        return results;
    }

    private async Task<Dictionary<long, AuditStatePair>> LoadStatesAsync(
        long[] eventIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, AuditStatePair>();
        var states = new List<AuditStateRow>();

        await using var connection = await OpenConnectionAsync(cancellationToken);

        var idList = string.Join(",", eventIds);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT id, event_id, side, content, status, memory_source FROM audit_state WHERE event_id IN ({idList})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                states.Add(new AuditStateRow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    new AuditMetadata
                    {
                        Content = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Status = reader.IsDBNull(4) ? null : reader.GetString(4),
                        MemorySource = reader.IsDBNull(5) ? null : reader.GetString(5)
                    }));
            }
        }

        var tags = new Dictionary<long, List<string>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT state_id, tag FROM audit_tags WHERE state_id IN (SELECT id FROM audit_state WHERE event_id IN ({idList})) ORDER BY state_id, ordinal";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var stateId = reader.GetInt64(0);
                if (!tags.TryGetValue(stateId, out var list))
                {
                    list = [];
                    tags[stateId] = list;
                }
                list.Add(reader.GetString(1));
            }
        }

        foreach (var row in states)
        {
            var complete = tags.TryGetValue(row.StateId, out var list)
                ? new AuditMetadata { Content = row.State.Content, Status = row.State.Status, MemorySource = row.State.MemorySource, Tags = list }
                : row.State;
            var pair = result.TryGetValue(row.EventId, out var existing) ? existing : new AuditStatePair(null, null);
            result[row.EventId] = row.Side == After ? new AuditStatePair(complete, pair.Previous) : new AuditStatePair(pair.After, complete);
        }

        return result;
    }

    /// <summary>
    /// A stored label read back as its typed value. The write sites store the
    /// kebab label, which for the single-word members is exactly the member name
    /// in lower case, so a case-insensitive parse is its inverse — and it
    /// therefore accepts the PascalCase spelling the previous writer left behind.
    /// </summary>
    private static T ParseLabel<T>(string label) where T : struct
        => Enum.TryParse(label, ignoreCase: true, out T value) ? value : default;

    /// <summary>
    /// A stored scope label read back as its typed value. The canonical spelling
    /// is "project-local", which no enum member name matches, so it goes through
    /// the scope parser; a row written before the label-vocabulary change holds
    /// the declared member name ("ProjectLocal"), which the parser lowercases to
    /// "projectlocal" and rejects. That legacy spelling is accepted as a fallback
    /// so an upgraded cache misreads no row.
    /// </summary>
    private static MemoryScopeKind? ParseScope(string label)
        => MemoryScopeParser.TryParseScope(label, out var scope)
            ? scope
            : Enum.TryParse(label, ignoreCase: true, out MemoryScopeKind legacy) ? legacy : null;

    private static AuditEvent WithState(AuditEvent e, AuditMetadata? after, AuditMetadata? previous) => new()
    {
        SchemaVersion = e.SchemaVersion,
        Timestamp = e.Timestamp,
        Trail = e.Trail,
        Category = e.Category,
        Action = e.Action,
        Actor = e.Actor,
        Source = e.Source,
        Scope = e.Scope,
        ProjectRoot = e.ProjectRoot,
        Target = e.Target,
        Outcome = e.Outcome,
        DurationMs = e.DurationMs,
        ByteSize = e.ByteSize,
        Hash = e.Hash,
        PrevHash = e.PrevHash,
        Query = e.Query,
        ResultCount = e.ResultCount,
        Path = e.Path,
        Matches = e.Matches,
        Pid = e.Pid,
        Metadata = after,
        PreviousMetadata = previous
    };

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// One machine-global database written by every Eling process, so every
    /// connection applies the WAL + busy-timeout pragmas — the same contract
    /// SqliteCodebaseIndex already established for its shared store, and the
    /// reason concurrent flushes from peer processes do not race on SQLITE_BUSY.
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
