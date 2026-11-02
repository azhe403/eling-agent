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

        // A rebuild deletes every row and reinserts the whole trail, and SQLite
        // hands freed pages back to its freelist rather than to the OS. Without
        // this, each startup grew the file monotonically: an audit trail whose
        // source JSONL was a couple of megabytes produced a 162 MB database of
        // which every table held zero rows, and mapping that file was what made
        // an idle backend hold hundreds of megabytes of working set.
        //
        // auto_vacuum must be set before the first table exists to take effect,
        // so a database created by an older build carries the old mode and needs
        // VACUUM once to adopt it. The pragma is cheap when it already applies.
        await EnableIncrementalAutoVacuumAsync(connection, cancellationToken);

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
                verb         TEXT NULL,
                truncated    INTEGER NULL,
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

        // Migration: older audit-index.db files predate the verb/truncated columns.
        // CREATE TABLE IF NOT EXISTS won't add them, so ALTER (ignoring "duplicate
        // column name" on already-migrated databases).
        foreach (var colDecl in new[] { "verb TEXT NULL", "truncated INTEGER NULL" })
        {
            try
            {
                await ExecuteAsync(connection, $"ALTER TABLE audit_events ADD COLUMN {colDecl};", cancellationToken);
            }
            catch (SqliteException)
            {
                // Column already exists — nothing to do.
            }
        }
    }

    public async Task IndexAsync(IReadOnlyList<AuditRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // One command per statement, prepared once and reused for every record in
        // the chunk. Creating a command plus its parameters per row was the single
        // largest allocation path in a rebuild: each AddWithValue marshals its
        // string argument to UTF-8, so a row cost ~23 such conversions. Reusing
        // the prepared statement drops that to setting values on existing
        // parameters, and SQLite parses the SQL once instead of once per row.
        await using var eventCommand = PrepareEventCommand(connection, transaction);
        await using var stateCommand = PrepareStateCommand(connection, transaction);
        await using var tagCommand = PrepareTagCommand(connection, transaction);

        foreach (var record in records)
        {
            var eventId = await InsertEventAsync(eventCommand, record, cancellationToken);
            await InsertStateAsync(stateCommand, tagCommand, eventId, After, record.Event.Metadata, cancellationToken);
            await InsertStateAsync(stateCommand, tagCommand, eventId, Previous, record.Event.PreviousMetadata, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static SqliteCommand PrepareEventCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events
                (timestamp, trail, category, action, actor, source, scope, project_root, target,
                 outcome, duration_ms, byte_size, hash, prev_hash,
                 query_text, result_count, verb, truncated, path, matches, pid, file, line)
            VALUES
                ($ts, $trail, $cat, $action, $actor, $source, $scope, $root, $target,
                 $outcome, $duration, $size, $hash, $prevhash,
                 $query, $count, $verb, $truncated, $path, $matches, $pid, $file, $line);
            SELECT last_insert_rowid();
            """;
        foreach (var name in new[]
                 {
                     "$ts", "$trail", "$cat", "$action", "$actor", "$source", "$scope", "$root",
                     "$target", "$outcome", "$duration", "$size", "$hash", "$prevhash", "$query",
                     "$count", "$verb", "$truncated", "$path", "$matches", "$pid", "$file", "$line",
                 })
        {
            command.Parameters.Add(new SqliteParameter(name, null));
        }
        command.Prepare();
        return command;
    }

    private static SqliteCommand PrepareStateCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_state (event_id, side, content, status, memory_source)
            VALUES ($event, $side, $content, $status, $source);
            SELECT last_insert_rowid();
            """;
        foreach (var name in new[] { "$event", "$side", "$content", "$status", "$source" })
        {
            command.Parameters.Add(new SqliteParameter(name, null));
        }
        command.Prepare();
        return command;
    }

    private static SqliteCommand PrepareTagCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO audit_tags (state_id, ordinal, tag) VALUES ($state, $ord, $tag);";
        foreach (var name in new[] { "$state", "$ord", "$tag" })
        {
            command.Parameters.Add(new SqliteParameter(name, null));
        }
        command.Prepare();
        return command;
    }

    private static async Task<long> InsertEventAsync(
        SqliteCommand command, AuditRecord record, CancellationToken cancellationToken)
    {
        var e = record.Event;
        command.Parameters["$ts"].Value = e.Timestamp.ToString("O");
        command.Parameters["$trail"].Value = AuditLabels.ToWireString(e.Trail);
        command.Parameters["$cat"].Value = AuditLabels.ToWireString(e.Category);
        command.Parameters["$action"].Value = e.Action;
        command.Parameters["$actor"].Value = e.Actor;
        command.Parameters["$source"].Value = e.Source;
        command.Parameters["$scope"].Value = (object?)(e.Scope.HasValue ? AuditLabels.ToWireString(e.Scope.Value) : null) ?? DBNull.Value;
        command.Parameters["$root"].Value = (object?)e.ProjectRoot ?? DBNull.Value;
        command.Parameters["$target"].Value = (object?)e.Target ?? DBNull.Value;
        command.Parameters["$outcome"].Value = AuditLabels.ToWireString(e.Outcome);
        command.Parameters["$duration"].Value = (object?)e.DurationMs ?? DBNull.Value;
        command.Parameters["$size"].Value = (object?)e.ByteSize ?? DBNull.Value;
        command.Parameters["$hash"].Value = (object?)e.Hash ?? DBNull.Value;
        command.Parameters["$prevhash"].Value = (object?)e.PrevHash ?? DBNull.Value;
        command.Parameters["$query"].Value = (object?)e.Query ?? DBNull.Value;
        command.Parameters["$count"].Value = (object?)e.ResultCount ?? DBNull.Value;
        command.Parameters["$verb"].Value = (object?)e.Verb ?? DBNull.Value;
        command.Parameters["$truncated"].Value = (object?)e.Truncated ?? DBNull.Value;
        command.Parameters["$path"].Value = (object?)e.Path ?? DBNull.Value;
        command.Parameters["$matches"].Value = (object?)e.Matches ?? DBNull.Value;
        command.Parameters["$pid"].Value = (object?)e.Pid ?? DBNull.Value;
        command.Parameters["$file"].Value = record.File;
        command.Parameters["$line"].Value = record.Line;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertStateAsync(
        SqliteCommand stateCommand, SqliteCommand tagCommand, long eventId,
        string side, AuditMetadata? state, CancellationToken cancellationToken)
    {
        if (state is null) return;

        stateCommand.Parameters["$event"].Value = eventId;
        stateCommand.Parameters["$side"].Value = side;
        stateCommand.Parameters["$content"].Value = (object?)state.Content ?? DBNull.Value;
        stateCommand.Parameters["$status"].Value = (object?)state.Status ?? DBNull.Value;
        stateCommand.Parameters["$source"].Value = (object?)state.MemorySource ?? DBNull.Value;
        var stateId = Convert.ToInt64(await stateCommand.ExecuteScalarAsync(cancellationToken));

        if (state.Tags is not { Count: > 0 }) return;
        for (var ordinal = 0; ordinal < state.Tags.Count; ordinal++)
        {
            tagCommand.Parameters["$state"].Value = stateId;
            tagCommand.Parameters["$ord"].Value = ordinal;
            tagCommand.Parameters["$tag"].Value = state.Tags[ordinal];
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Switches the index file to incremental auto-vacuum so pages freed by a
    /// rebuild can be released instead of accumulating. SQLite only honours the
    /// setting when it is written before the first table is created, so an
    /// existing file in the old mode is converted with a one-off VACUUM.
    /// </summary>
    private static async Task EnableIncrementalAutoVacuumAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        // 2 == INCREMENTAL. On a fresh database this is the value later writes
        // record in the header; on an existing one it is inert until VACUUM.
        await ExecuteAsync(connection, "PRAGMA auto_vacuum=INCREMENTAL;", cancellationToken);

        await using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA auto_vacuum;";
        var mode = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken));
        if (mode == 2) return;

        // Conversion runs outside a transaction and rewrites the file once. Only
        // reached for databases an earlier build created.
        await ExecuteAsync(connection, "VACUUM;", cancellationToken);
    }

    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        // Rebuild incrementally rather than from scratch. The old pass deleted
        // every row and reinserted the whole trail on every startup, which is
        // what let an index over a couple of megabytes of JSONL grow to 162 MB
        // of freelist pages: each pass wrote a whole copy and handed the previous
        // copy back to SQLite, never to the OS.
        //
        // A day file is immutable once rotation compresses it, and the open day
        // only ever grows, so "file present with at least as many lines as it has
        // now" is a sound seen-marker. Only files that are new, or whose line
        // count grew, are re-read; on a quiet startup nothing is re-read at all.
        var indexedLines = await ReadIndexedLineCountsAsync(cancellationToken);

        foreach (var trail in new[] { AuditTrail.Change, AuditTrail.Access })
        {
            // The trail directory comes from the store rather than a second
            // hard-coded "changes"/"access" mapping, and every day form is read
            // through the shared day-file reader: a rebuild after rotation must
            // still see the closed days, and Directory.GetFiles(dir, "*.jsonl")
            // does not match "*.jsonl.gz".
            var dir = Path.GetDirectoryName(store.GetCurrentFilePath(trail, DateTimeOffset.UnixEpoch))!;

            // Streamed day by day: the previous version materialised every entry
            // of every day into one list before writing a single row.
            await foreach (var day in AuditDayFiles.ReadAsync(dir, trail, cancellationToken))
            {
                // The seen-marker and the delete scope must both be trail-qualified.
                // A bare file name collides: changes/2026-10-23.jsonl.gz and
                // access/2026-10-23.jsonl.gz share a name, so keying on it alone
                // made the second trail look already-indexed and skip it — 30k
                // access entries silently stayed out of the index.
                var sourceKey = TrailQualifiedKey(trail, day.SourceFile);
                if (indexedLines.TryGetValue(sourceKey, out var already) && already >= day.Entries.Count)
                {
                    continue;
                }

                // A file that changed is replaced wholesale: its rows are keyed by
                // (file, line), so a longer file renumbers nothing and a shorter
                // one must not leave stale tail rows behind.
                await DeleteFileRowsAsync(sourceKey, cancellationToken);

                foreach (var chunk in ToRecords(sourceKey, day.Entries).Chunk(500))
                {
                    await IndexAsync(chunk, cancellationToken);
                }

                indexedLines[sourceKey] = day.Entries.Count;
            }
        }

        // Hand the pages the deletes above freed back to the OS. Under
        // auto_vacuum=INCREMENTAL this is what actually shrinks the file;
        // without it the freelist keeps them reserved and the file only ever
        // grows. Best-effort: a busy shared store must not fail the rebuild.
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await ExecuteAsync(connection, "PRAGMA incremental_vacuum;", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The index is a rebuildable cache; leaving the file unshrunk is a
            // size problem, not a correctness one.
        }
    }

    /// <summary>
    /// The key a day file is indexed and remembered under: its trail plus its
    /// name, so the two trails' same-named day files never collide.
    /// </summary>
    private static string TrailQualifiedKey(AuditTrail trail, string sourceFile)
        => AuditLabels.ToWireString(trail) + "/" + Path.GetFileName(sourceFile);

    /// <summary>
    /// How many lines each indexed file already contributed, keyed by the
    /// trail-qualified source key. One grouped query replaces a per-file count
    /// probe, and an absent key means the file was never indexed.
    /// </summary>
    private async Task<Dictionary<string, int>> ReadIndexedLineCountsAsync(CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT file, MAX(line) FROM audit_events GROUP BY file;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }
        return counts;
    }

    /// <summary>
    /// Drops every row that came from <paramref name="fileName"/>, children
    /// first so the cascade does not have to do it row by row.
    /// </summary>
    private async Task DeleteFileRowsAsync(string fileName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM audit_tags
            WHERE state_id IN (
                SELECT s.id FROM audit_state s
                JOIN audit_events e ON e.id = s.event_id
                WHERE e.file = $file);
            DELETE FROM audit_state
            WHERE event_id IN (SELECT id FROM audit_events WHERE file = $file);
            DELETE FROM audit_events WHERE file = $file;
            """;
        command.Parameters.AddWithValue("$file", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IEnumerable<AuditRecord> ToRecords(string fileName, IReadOnlyList<AuditEvent> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            yield return new AuditRecord(fileName, i + 1, entries[i]);
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
                       query_text, result_count, verb, truncated, path, matches, pid
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
                        Verb = reader.IsDBNull(19) ? null : reader.GetString(19),
                        Truncated = reader.IsDBNull(20) ? null : reader.GetInt32(20) != 0,
                        Path = reader.IsDBNull(21) ? null : reader.GetString(21),
                        Matches = reader.IsDBNull(22) ? null : reader.GetInt32(22),
                        Pid = reader.IsDBNull(23) ? null : reader.GetInt32(23)
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
        Verb = e.Verb,
        Truncated = e.Truncated,
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
