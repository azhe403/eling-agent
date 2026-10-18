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

        group.MapGet("/", async (IDatabaseConnectionStore store, CancellationToken ct) =>
        {
            var rows = await store.ListAsync(ct);
            var views = rows.Select(ToView).ToList();
            return Results.Ok(views);
        });

        group.MapGet("/{id}", async (string id, IDatabaseConnectionStore store, CancellationToken ct) =>
        {
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();
            return Results.Ok(ToView(row));
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
                var conn = DatabaseConnection.Create(
                    req.Name,
                    req.Provider,
                    req.ConnectionString,
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
            var connStr = string.IsNullOrWhiteSpace(req.ConnectionString) ? existing.ConnectionString : req.ConnectionString!;
            var allow = req.AllowAgentMutations ?? existing.AllowAgentMutations;
            var require = req.RequireApprovalForMutations ?? existing.RequireApprovalForMutations;

            try
            {
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

        group.MapPost("/test-connection", async (
            CreateDatabaseConnectionRequest req,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            DatabaseConnection conn;
            try
            {
                conn = DatabaseConnection.Create(
                    string.IsNullOrWhiteSpace(req.Name) ? "test@local" : req.Name,
                    req.Provider,
                    req.ConnectionString,
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
            IDatabaseConnectionStore store,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();

            try
            {
                var adapter = DatabaseAdapterFactory.CreateAdapter(row);
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
            IDatabaseConnectionStore store,
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

            try
            {
                var schema = await adapter.GetSchemaAsync(ct);
                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    var filtered = schema.Tables.Where(t =>
                        string.Equals(t.Name, tableName, StringComparison.Ordinal)).ToList();
                    schema = new DatabaseSchemaInfo(filtered);
                }
                return Results.Ok(schema);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Schema failed for {Id}", id);
                return Results.Problem(statusCode: 502, title: "schema failed", detail: ex.Message);
            }
        });

        group.MapPost("/{id}/query", async (
            string id,
            DatabaseQueryRequest req,
            IDatabaseConnectionStore store,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();

            var classification = SqlSafety.Classify(req.Sql);
            if (classification.Verdict != SqlVerdict.ReadOnly)
            {
                return Results.BadRequest(classification.Reason);
            }

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

            var maxRows = req.MaxRows;
            if (maxRows < 1) maxRows = 1;
            if (maxRows > 1000) maxRows = 1000;

            try
            {
                var result = await adapter.QueryAsync(req.Sql, maxRows, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Query failed for {Id}", id);
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
                adapter = DatabaseAdapterFactory.CreateAdapter(row);
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Database adapter not supported for {Id}", id);
                return Results.BadRequest(ex.Message);
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

        group.MapGet("/detect", (
            AppSettingsConnectionScanner scanner) =>
        {
            var detected = scanner.Scan();
            var dtos = detected.Select(d => new DetectedConnectionDto(
                d.SettingsKey,
                d.Name,
                d.MaskedConnectionString,
                d.Provider,
                d.RejectionReason)).ToList();
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
        c.LastConnectedAt);
}
