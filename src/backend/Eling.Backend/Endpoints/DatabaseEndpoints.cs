using Eling.Backend.Database;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Database;
using Eling.Core.Database.Adapters;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Endpoints;

public static class DatabaseEndpoints
{
    public static WebApplication MapDatabaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/databases");

        group.MapGet("/", async (IDatabaseConnectionStore store, IAuditLogger auditLogger, CancellationToken ct) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rows = await store.ListAsync(ct);
            var views = rows.Select(ToView).ToList();
            sw.Stop();
            await auditLogger.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.ListConnections,
                Actor = "dashboard",
                Outcome = AuditOutcome.Success,
                ResultCount = rows.Count,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, ct);
            return Results.Ok(views);
        });

        group.MapGet("/{id}", async (string id, IDatabaseConnectionStore store, IAuditLogger auditLogger, CancellationToken ct) =>
        {
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var view = ToView(row);
            sw.Stop();
            await auditLogger.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.GetConnection,
                Actor = "dashboard",
                Target = id,
                Outcome = AuditOutcome.Success,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, ct);
            return Results.Ok(view);
        });

        group.MapPost("/", async (
            CreateDatabaseConnectionRequest req,
            IDatabaseConnectionStore store,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            try
            {
                var connStr = ResolveConnectionString(req.Provider, req.ConnectionString, req.Manual, null);
                var conn = DatabaseConnection.Create(
                    req.Name,
                    req.Provider,
                    connStr,
                    req.Environment,
                    req.AllowAgentMutations,
                    req.RequireApprovalForMutations);
                await store.UpsertAsync(conn, ct);
                return Results.Created($"/api/databases/{conn.Id}", ToView(conn));
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Create database connection failed");
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPut("/{id}", async (
            string id,
            UpdateDatabaseConnectionRequest req,
            IDatabaseConnectionStore store,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var existing = await store.FindAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var name = req.Name ?? existing.Name;
            var env = req.Environment ?? existing.Environment;
            var allow = req.AllowAgentMutations ?? existing.AllowAgentMutations;
            var require = req.RequireApprovalForMutations ?? existing.RequireApprovalForMutations;

            try
            {
                var connStr = ResolveConnectionString(existing.Provider, req.ConnectionString, req.Manual, existing.ConnectionString);
                var updated = existing with
                {
                    Name = name,
                    Environment = env,
                    ConnectionString = connStr,
                    AllowAgentMutations = allow,
                    RequireApprovalForMutations = require
                };
                await store.UpsertAsync(updated, ct);
                return Results.Ok(ToView(updated));
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Update database connection failed");
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapDelete("/{id}", async (string id, IDatabaseConnectionStore store, CancellationToken ct) =>
        {
            var ok = await store.RemoveAsync(id, ct);
            if (!ok) return Results.NotFound();
            return Results.NoContent();
        });

        group.MapPost("/list-databases", async (
            CreateDatabaseConnectionRequest req,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            // The credentials are the ones just typed into the form, not a
            // saved connection: this answers "which databases are on that
            // server" before anything is registered.
            DatabaseConnection conn;
            try
            {
                var connStr = ResolveConnectionString(req.Provider, req.ConnectionString, req.Manual, null);
                conn = DatabaseConnection.Create(
                    "list@local",
                    req.Provider,
                    connStr,
                    req.Environment,
                    req.AllowAgentMutations,
                    req.RequireApprovalForMutations);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "List databases validation failed");
                return Results.BadRequest(ex.Message);
            }

            try
            {
                var adapter = DatabaseAdapterFactory.CreateAdapter(conn);
                var databases = await adapter.ListDatabasesAsync(ct);
                return Results.Ok(databases);
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Database adapter not supported");
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                // The message names the failure category only - the exception
                // text can carry the user name and the host.
                logger.LogWarning(ex, "Listing databases failed");
                return Results.Problem(statusCode: 502, title: "databases failed", detail: "could not read the databases on this server");
            }
        });

        group.MapPost("/test-connection", async (
            CreateDatabaseConnectionRequest req,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            DatabaseConnection conn;
            try
            {
                var connStr = ResolveConnectionString(req.Provider, req.ConnectionString, req.Manual, null);
                conn = DatabaseConnection.Create(
                    string.IsNullOrWhiteSpace(req.Name) ? "test@local" : req.Name,
                    req.Provider,
                    connStr,
                    req.Environment,
                    req.AllowAgentMutations,
                    req.RequireApprovalForMutations);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Test connection validation failed");
                return Results.BadRequest(ex.Message);
            }

            try
            {
                var adapter = DatabaseAdapterFactory.CreateAdapter(conn);
                var result = await adapter.TestAsync(ct);
                return Results.Ok(new DatabaseTestResponse(result.Ok, result.Message, result.LatencyMs));
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Database adapter not supported");
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Test connection failed");
                return Results.Ok(new DatabaseTestResponse(false, "connection failed", 0));
            }
        });

        group.MapPost("/{id}/test", async (
            string id,
            TestDatabaseConnectionRequest? req,
            IDatabaseConnectionStore store,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();

            // No body: the stored connection is tested as it is. With a body the
            // caller is trying out an edit, so the entered fields are layered
            // over the stored connection - a blank password keeps the stored
            // one.
            var connectionString = row.ConnectionString;
            if (req is not null)
            {
                try
                {
                    connectionString = ResolveConnectionString(row.Provider, req.ConnectionString, req.Manual, row.ConnectionString);
                }
                catch (ArgumentException ex)
                {
                    logger.LogWarning(ex, "Test connection validation failed for {Id}", id);
                    return Results.BadRequest(ex.Message);
                }
            }

            try
            {
                var adapter = DatabaseAdapterFactory.CreateAdapter(row with { ConnectionString = connectionString });
                var result = await adapter.TestAsync(ct);
                if (result.Ok)
                {
                    await store.MarkConnectedAsync(id, DateTimeOffset.UtcNow, ct);
                }
                return Results.Ok(new DatabaseTestResponse(result.Ok, result.Message, result.LatencyMs));
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Database adapter not supported for {Id}", id);
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Database test failed for {Id}", id);
                return Results.Ok(new DatabaseTestResponse(false, "connection failed", 0));
            }
        });

        group.MapGet("/{id}/schema", async (
            string id,
            string? tableName,
            string? database,
            IDatabaseConnectionStore store,
            IAuditLogger auditLogger,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();

            IDatabaseAdapter adapter;
            try
            {
                adapter = DatabaseAdapterFactory.CreateAdapter(row);
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Database adapter not supported for {Id}", id);
                return Results.BadRequest(ex.Message);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // A server-level connection - one registered without a
                // database - has no default schema, so it lists the databases
                // on the server instead of the tables of one of them.
                var schema = string.IsNullOrWhiteSpace(database) && IsServerLevel(row)
                    ? new DatabaseSchemaInfo([]) { Databases = await adapter.ListDatabasesAsync(ct) }
                    : await adapter.GetSchemaAsync(database, ct);

                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    var filtered = schema.Tables.Where(t =>
                        string.Equals(t.Name, tableName, StringComparison.Ordinal)).ToList();
                    schema = schema with { Tables = filtered };
                }
                sw.Stop();
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.GetSchema,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Success,
                    Query = tableName,
                    DurationMs = (int)sw.ElapsedMilliseconds
                }, ct);
                return Results.Ok(schema);
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogWarning(ex, "Schema failed for {Id}", id);
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.GetSchema,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Error,
                    Query = tableName,
                    DurationMs = (int)sw.ElapsedMilliseconds
                }, ct);
                return Results.Problem(statusCode: 502, title: "schema failed", detail: ex.Message);
            }
        });

        group.MapPost("/{id}/databases", async (
            string id,
            TestDatabaseConnectionRequest? req,
            IDatabaseConnectionStore store,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();

            // The stored password is not in the form, so the entered fields
            // are layered over the stored connection - a blank password keeps
            // the stored one.
            var connectionString = row.ConnectionString;
            if (req is not null)
            {
                try
                {
                    connectionString = ResolveConnectionString(row.Provider, req.ConnectionString, req.Manual, row.ConnectionString);
                }
                catch (ArgumentException ex)
                {
                    logger.LogWarning(ex, "List databases validation failed for {Id}", id);
                    return Results.BadRequest(ex.Message);
                }
            }

            try
            {
                var adapter = DatabaseAdapterFactory.CreateAdapter(row with { ConnectionString = connectionString });
                var databases = await adapter.ListDatabasesAsync(ct);
                return Results.Ok(databases);
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Database adapter not supported for {Id}", id);
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Listing databases failed for {Id}", id);
                return Results.Problem(statusCode: 502, title: "databases failed", detail: "could not read the databases on this server");
            }
        });

        group.MapPost("/{id}/query", async (
            string id,
            DatabaseQueryRequest req,
            IDatabaseConnectionStore store,
            IAuditLogger auditLogger,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();

            var classification = SqlSafety.Classify(req.Sql);
            if (classification.Verdict != SqlVerdict.ReadOnly)
            {
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.Query,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Denied,
                    Query = req.Sql,
                    Verb = classification.MutationVerb
                }, ct);
                return Results.BadRequest(classification.Reason);
            }

            IDatabaseAdapter adapter;
            try
            {
                adapter = DatabaseAdapterFactory.CreateAdapter(Target(row, req.Database));
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Database adapter not supported for {Id}", id);
                return Results.BadRequest(ex.Message);
            }
            catch (ArgumentException ex)
            {
                // The stored connection string cannot be read - a broken
                // registration rather than a bad request, so it answers like
                // the provider failures below instead of crashing.
                logger.LogWarning(ex, "Stored connection string is unreadable for {Id}", id);
                return Results.Problem(statusCode: 502, title: "query failed", detail: ex.Message);
            }

            var maxRows = req.MaxRows;
            if (maxRows < 1) maxRows = 1;
            if (maxRows > 1000) maxRows = 1000;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await adapter.QueryAsync(req.Sql, maxRows, ct);
                sw.Stop();
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.Query,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Success,
                    Query = req.Sql,
                    ResultCount = result.Rows.Count,
                    DurationMs = (int)sw.ElapsedMilliseconds
                }, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogWarning(ex, "Query failed for {Id}", id);
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.Query,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Error,
                    Query = req.Sql,
                    DurationMs = (int)sw.ElapsedMilliseconds
                }, ct);
                return Results.Problem(statusCode: 502, title: "query failed", detail: ex.Message);
            }
        });

        group.MapPost("/{id}/mutate", async (
            string id,
            DatabaseMutateRequest req,
            IDatabaseConnectionStore store,
            ILoggerFactory loggerFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();

            if (row.Environment == DatabaseEnvironment.ReadOnly)
            {
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Change,
                    Category = AuditCategory.Database,
                    Action = "db.mutate",
                    Target = row.Id,
                    Actor = "dashboard",
                    Outcome = AuditOutcome.Denied,
                    Query = req.Sql,
                }, ct);
                return Results.Json(new { error = "mutations not allowed on read-only connection" }, statusCode: 403);
            }

            var classification = SqlSafety.Classify(req.Sql);
            if (classification.Verdict != SqlVerdict.Mutation)
            {
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Change,
                    Category = AuditCategory.Database,
                    Action = "db.mutate",
                    Target = row.Id,
                    Actor = "dashboard",
                    Outcome = AuditOutcome.Denied,
                    Verb = classification.MutationVerb,
                    Query = req.Sql,
                }, ct);
                return Results.BadRequest(classification.Reason ?? "not a mutation statement");
            }

            IDatabaseAdapter adapter;
            try
            {
                adapter = DatabaseAdapterFactory.CreateAdapter(Target(row, req.Database));
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Database adapter not supported for {Id}", id);
                return Results.BadRequest(ex.Message);
            }
            catch (ArgumentException ex)
            {
                // The stored connection string cannot be read - a broken
                // registration rather than a bad request, so it answers like
                // the provider failures below instead of crashing.
                logger.LogWarning(ex, "Stored connection string is unreadable for {Id}", id);
                return Results.Problem(statusCode: 502, title: "mutate failed", detail: ex.Message);
            }

            var maxAffected = req.MaxAffectedRows;
            if (maxAffected < 1) maxAffected = 1;
            if (maxAffected > 10000) maxAffected = 10000;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var affected = await adapter.ExecuteAsync(req.Sql, ct);
                var truncated = affected > maxAffected;
                if (truncated) affected = maxAffected;
                sw.Stop();
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Change,
                    Category = AuditCategory.Database,
                    Action = "db.mutate",
                    Target = row.Id,
                    Actor = "dashboard",
                    Outcome = AuditOutcome.Success,
                    Verb = classification.MutationVerb,
                    Query = req.Sql,
                    ResultCount = affected,
                    Truncated = truncated,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                }, ct);
                return Results.Ok(new DatabaseMutateResponse(affected, truncated));
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogWarning(ex, "Mutate failed for {Id}", id);
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Change,
                    Category = AuditCategory.Database,
                    Action = "db.mutate",
                    Target = row.Id,
                    Actor = "dashboard",
                    Outcome = AuditOutcome.Error,
                    Verb = classification.MutationVerb,
                    Query = req.Sql,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                }, ct);
                return Results.Problem(statusCode: 502, title: "mutate failed", detail: ex.Message);
            }
        });

        group.MapGet("/detect", async (
            AppSettingsConnectionScanner scanner,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var detected = scanner.Scan();
            var dtos = detected.Select(d => new DetectedConnectionDto(
                d.SettingsKey,
                d.Name,
                d.MaskedConnectionString,
                d.Provider,
                d.RejectionReason,
                d.SourceFile)).ToList();
            sw.Stop();
            await auditLogger.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.DetectConnections,
                Actor = "dashboard",
                Outcome = AuditOutcome.Success,
                ResultCount = detected.Count,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, ct);
            return Results.Ok(dtos);
        });

        group.MapPost("/import", async (
            ImportRequest req,
            DatabaseConnectionImporter importer,
            CancellationToken ct) =>
        {
            var result = await importer.ImportAsync(req.SettingsKeys ?? [], ct);
            return Results.Ok(result);
        });

        return app;
    }

    private static DatabaseConnectionView ToView(DatabaseConnection c) => new(
        c.Id,
        c.Name,
        c.Provider,
        c.Environment,
        !string.IsNullOrWhiteSpace(c.ConnectionString),
        c.AllowAgentMutations,
        c.RequireApprovalForMutations,
        c.CreatedAt,
        c.LastConnectedAt,
        ToDetails(c),
        IsServerLevel(c));

    /// <summary>
    /// The stored connection read back into fields so the dashboard can
    /// pre-fill its form. It carries no password - the credential never
    /// leaves the backend.
    /// </summary>
    private static DatabaseConnectionDetails? ToDetails(DatabaseConnection c)
    {
        var fields = DatabaseConnectionStringFactory.Parse(c.Provider, c.ConnectionString);
        if (fields is null) return null;

        return new DatabaseConnectionDetails(
            fields.Host,
            fields.Port,
            fields.Database,
            fields.Username,
            fields.FilePath);
    }

    /// <summary>
    /// A connection that names no database reaches every database on the
    /// server, so the dashboard lists them instead of the tables of one.
    /// </summary>
    private static bool IsServerLevel(DatabaseConnection c) =>
        DatabaseConnectionStringFactory.IsServerLevel(c.Provider, c.ConnectionString);

    /// <summary>
    /// Points the connection at a database, so an unqualified table name
    /// resolves. A connection that already has its own database keeps it
    /// unless the caller names another one.
    /// </summary>
    private static DatabaseConnection Target(DatabaseConnection connection, string? database)
    {
        if (string.IsNullOrWhiteSpace(database)) return connection;

        return connection with
        {
            ConnectionString = DatabaseConnectionStringFactory.WithDatabase(
                connection.Provider,
                connection.ConnectionString,
                database)
        };
    }

    /// <summary>
    /// Works out which connection string to use. An explicit connection string
    /// wins; otherwise the fields the person typed are built into one, layered
    /// over what is already stored so a blank password keeps the stored one.
    /// </summary>
    private static string ResolveConnectionString(
        DatabaseProvider provider,
        string? connectionString,
        ManualConnectionFields? manual,
        string? storedConnectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        if (manual is null)
        {
            return storedConnectionString ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(storedConnectionString))
        {
            // Layered onto the stored string, so the options no field covers
            // - an SSL mode, a charset - survive the edit.
            return DatabaseConnectionStringFactory.Update(provider, storedConnectionString, manual);
        }

        return DatabaseConnectionStringFactory.Build(provider, manual);
    }
}
