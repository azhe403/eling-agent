using Eling.Backend.Bootstrap;
using Eling.Backend.Database;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Database;
using Eling.Core.Database.Adapters;
using Eling.Core.Database.Browse;
using StackExchange.Redis;
using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace Eling.Backend.Endpoints;

public static class DatabaseEndpoints
{
    public static WebApplication MapDatabaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/databases");

        group.MapGet("/", async (
            IDatabaseConnectionStore store,
            IAuditLogger auditLogger,
            CancellationToken ct,
            string? sort = null,
            string? order = null) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rows = await store.ListAsync(ct);
            // The server owns the order: an unknown sort or direction falls
            // back to the default rather than refusing the request.
            DatabaseConnectionSort.TryParseKey(sort, out var sortKey);
            DatabaseConnectionSort.TryParseOrder(order, out var sortOrder);
            var ordered = DatabaseConnectionSort.Apply(rows, sortKey, sortOrder);
            var views = ordered.Select(ToView).ToList();
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

        group.MapGet("/{id}", async (string id, HttpContext httpContext, IDatabaseConnectionStore store, IAuditLogger auditLogger, CancellationToken ct) =>
        {
            var row = await store.FindAsync(id, ct);
            if (row is null) return ConnectionNotFoundResult.Create(id, httpContext);
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
            HttpContext httpContext,
            UpdateDatabaseConnectionRequest req,
            IDatabaseConnectionStore store,
            IDatabaseReadCache readCache,
            IRedisReadCache redisReadCache,
            IRedisConnectionPool redisPool,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var existing = await store.FindAsync(id, ct);
            if (existing is null) return ConnectionNotFoundResult.Create(id, httpContext);

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
                readCache.InvalidateConnection(id);
                redisReadCache.InvalidateConnection(id);
                // A changed connection string means a different server, or a
                // new password: the pooled multiplexer still points at the
                // old credentials, so it has to go with the old row.
                redisPool.Invalidate(id);
                return Results.Ok(ToView(updated));
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Update database connection failed");
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapDelete("/{id}", async (string id, HttpContext httpContext, IDatabaseConnectionStore store, IDatabaseReadCache readCache, IRedisReadCache redisReadCache, IRedisConnectionPool redisPool, CancellationToken ct) =>
        {
            var ok = await store.RemoveAsync(id, ct);
            if (!ok) return ConnectionNotFoundResult.Create(id, httpContext);
            readCache.InvalidateConnection(id);
            redisReadCache.InvalidateConnection(id);
            // The row is gone; any pooled multiplexer it left behind would
            // otherwise sit in memory until the idle evictor gets it.
            redisPool.Invalidate(id);
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
            var connStr = ResolveConnectionString(req.Provider, req.ConnectionString, req.Manual, null);
            DatabaseConnection conn;
            try
            {
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

            var adapterResult = TryCreateAdapter(conn, logger);
            if (adapterResult.Error is not null) return adapterResult.Error;
            var adapter = adapterResult.Adapter!;

            try
            {
                var databases = await adapter!.ListDatabasesAsync(ct);
                return Results.Ok(databases);
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
            IRedisConnectionPool redisPool,
            CancellationToken ct = default) =>
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

            var adapterResult = TryCreateAdapter(conn, logger);
            if (adapterResult.Error is not null) return adapterResult.Error;
            var adapter = adapterResult.Adapter!;

            try
            {
                if (conn.Provider == DatabaseProvider.Redis)
                {
                    return Results.Ok(await TestRedisAsync(redisPool, conn, logger, ct));
                }

                var result = await adapter!.TestAsync(ct);
                return Results.Ok(new DatabaseTestResponse(result.Ok, result.Message, result.LatencyMs));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Test connection failed");
                return Results.Ok(new DatabaseTestResponse(false, "connection failed", 0));
            }
        });

        group.MapPost("/{id}/test", async (
            string id,
            HttpContext httpContext,
            TestDatabaseConnectionRequest? req,
            IDatabaseConnectionStore store,
            ILoggerFactory loggerFactory,
            IRedisConnectionPool redisPool,
            CancellationToken ct = default) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return ConnectionNotFoundResult.Create(id, httpContext);

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

            var adapterResult = TryCreateAdapter(row with { ConnectionString = connectionString }, logger);
            if (adapterResult.Error is not null) return adapterResult.Error;
            var adapter = adapterResult.Adapter!;

            try
            {
                if (row.Provider == DatabaseProvider.Redis)
                {
                    var conn2 = row with { ConnectionString = connectionString };
                    var tested = await TestRedisAsync(redisPool, conn2, logger, ct);
                    if (tested.Ok)
                    {
                        await store.MarkConnectedAsync(id, DateTimeOffset.UtcNow, ct);
                    }
                    return Results.Ok(tested);
                }

                var result = await adapter!.TestAsync(ct);
                if (result.Ok)
                {
                    await store.MarkConnectedAsync(id, DateTimeOffset.UtcNow, ct);
                }
                return Results.Ok(new DatabaseTestResponse(result.Ok, result.Message, result.LatencyMs));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Database test failed for {Id}", id);
                return Results.Ok(new DatabaseTestResponse(false, "connection failed", 0));
            }
        });

        group.MapGet("/{id}/schema", async (
            string id,
            HttpContext httpContext,
            string? tableName,
            string? database,
            bool? refresh,
            IDatabaseConnectionStore store,
            IDatabaseReadService readService,
            IAuditLogger auditLogger,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return ConnectionNotFoundResult.Create(id, httpContext);
            if (row.Provider == DatabaseProvider.Redis)
            {
                return Results.BadRequest(RedisRequirement.NotSql(row, "use /keys"));
            }

            var adapterResult = TryCreateAdapter(row, logger);
            if (adapterResult.Error is not null) return adapterResult.Error;
            var adapter = adapterResult.Adapter!;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var schema = await readService.GetSchemaAsync(row, database, ct, preferCache: refresh != true);

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
                    DurationMs = (int)sw.ElapsedMilliseconds,
                    FromCache = schema.FromCache
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
            HttpContext httpContext,
            TestDatabaseConnectionRequest? req,
            bool? refresh,
            IDatabaseConnectionStore store,
            IDatabaseReadService readService,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return ConnectionNotFoundResult.Create(id, httpContext);

            // The stored password is not in the form, so the entered fields
            // are layered over the stored connection - a blank password keeps
            // the stored one. With no override fields at all, this is the same
            // read the tree makes on every paint, so it goes through the cache.
            if (req is null || (req.ConnectionString is null && req.Manual is null))
            {
                try
                {
                    var databases = await readService.ListDatabasesAsync(row, ct, preferCache: refresh != true);
                    return Results.Ok(databases);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Listing databases failed for {Id}", id);
                    return Results.Problem(statusCode: 502, title: "databases failed", detail: "could not read the databases on this server");
                }
            }

            var connectionString = row.ConnectionString;
            try
            {
                connectionString = ResolveConnectionString(row.Provider, req.ConnectionString, req.Manual, row.ConnectionString);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "List databases validation failed for {Id}", id);
                return Results.BadRequest(ex.Message);
            }

            var adapterResult = TryCreateAdapter(row with { ConnectionString = connectionString }, logger);
            if (adapterResult.Error is not null) return adapterResult.Error;
            var adapter = adapterResult.Adapter!;

            try
            {
                var databases = await adapter!.ListDatabasesAsync(ct);
                return Results.Ok(databases);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Listing databases failed for {Id}", id);
                return Results.Problem(statusCode: 502, title: "databases failed", detail: "could not read the databases on this server");
            }
        });

        group.MapPost("/{id}/browse", async (
            string id,
            HttpContext httpContext,
            BrowseRequest req,
            IDatabaseConnectionStore store,
            IDatabaseReadService readService,
            IAuditLogger auditLogger,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return ConnectionNotFoundResult.Create(id, httpContext);
            if (row.Provider == DatabaseProvider.Redis)
            {
                return Results.BadRequest(RedisRequirement.NotSql(row, "use /keys"));
            }
            if (string.IsNullOrWhiteSpace(req.Table))
            {
                return Results.BadRequest("table is required");
            }
            var tableName = req.Table.Trim();

            var page = req.Page < 1 ? 1 : req.Page;
            var limit = Math.Clamp(req.Limit, 1, 1000);
            var preferCache = !req.Refresh;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Validation against the schema: table must exist, columns
            // referenced by filter or sort must exist. If the schema itself
            // cannot be read (DB down, no snapshot), the browse proceeds
            // with quoting alone - identifiers are safe by doubling, and a
            // cached snapshot must still be viewable offline.
            var schemaAvailable = true;
            var tableExists = false;
            var columnTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var schema = await readService.GetSchemaAsync(row, req.Database, ct, preferCache: preferCache);
                var table = schema.Tables.FirstOrDefault(t =>
                    string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
                if (table is not null)
                {
                    tableExists = true;
                    foreach (var c in table.Columns)
                    {
                        if (!string.IsNullOrEmpty(c.Name))
                        {
                            columnTypes[c.Name] = c.DataType ?? "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Schema read skipped for browse validation of {Id}; proceeding with quoting only.", id);
                schemaAvailable = false;
            }

            if (schemaAvailable && !tableExists)
            {
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.Browse,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Denied,
                    Query = tableName,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                }, ct);
                return Results.BadRequest($"table '{tableName}' not found");
            }

            // A filter row with an unknown logic would silently change the
            // fold, so it is named, not silently mapped.
            var filters = (req.Filters ?? [])
                .Select(f => new BrowseFilter(f.Column ?? "", (f.Op ?? "").Trim().ToLowerInvariant(), f.Value, f.Enabled, f.Logic ?? "first"))
                .ToList();
            var sorts = (req.Sort ?? [])
                .Select(s => new BrowseSort(s.Column ?? "", (s.Order ?? "asc").Trim().ToLowerInvariant()))
                .ToList();
            foreach (var f in filters)
            {
                var l = (f.Logic ?? "first").Trim().ToLowerInvariant();
                if (l is not "first" and not "and" and not "or")
                {
                    await auditLogger.RecordAsync(new AuditEvent
                    {
                        Trail = AuditTrail.Access,
                        Category = AuditCategory.Database,
                        Action = DatabaseAuditActions.Browse,
                        Actor = "dashboard",
                        Target = id,
                        Outcome = AuditOutcome.Denied,
                        Query = tableName,
                        DurationMs = (int)sw.ElapsedMilliseconds,
                    }, ct);
                    return Results.BadRequest($"unknown logic '{f.Logic}'");
                }
            }

            BrowsePlan plan;
            try
            {
                plan = BrowseQueryBuilder.Build(
                    row.Provider,
                    tableName,
                    filters,
                    sorts,
                    req.ManualCondition,
                    columnTypes,
                    schemaAvailable,
                    page,
                    limit);
            }
            catch (BrowseQueryException ex)
            {
                sw.Stop();
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.Browse,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Denied,
                    Query = tableName,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                }, ct);
                return Results.BadRequest(ex.Message);
            }

            // Defense in depth: the built statement must be read-only. The
            // builder can only emit SELECTs; anything else means a builder
            // defect, not caller input - 500, audited, never executed.
            if (SqlSafety.Classify(plan.Rows.Sql).Verdict != SqlVerdict.ReadOnly
                || SqlSafety.Classify(plan.Count.Sql).Verdict != SqlVerdict.ReadOnly)
            {
                sw.Stop();
                logger.LogError("Browse statement for {Id} is not read-only; refusing to execute.", id);
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.Browse,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Error,
                    Query = tableName,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                }, ct);
                return Results.Problem(statusCode: 500, title: "browse failed", detail: "the assembled browse statement is not read-only");
            }

            try
            {
                var rows = await readService.QueryAsync(
                    row, req.Database, plan.Rows.Sql, limit, ct,
                    preferCache: preferCache, parameters: plan.Rows.Parameters);
                var count = await readService.QueryAsync(
                    row, req.Database, plan.Count.Sql, 1, ct,
                    preferCache: preferCache, parameters: plan.Count.Parameters);

                // Half a response is worse than no response: when the rows
                // are served but the count is not, the whole call fails.
                var totalRaw = count.Rows.Count > 0 && count.Rows[0].Count > 0 ? count.Rows[0][0] : null;
                if (totalRaw is null
                    || !long.TryParse(
                        Convert.ToString(totalRaw, CultureInfo.InvariantCulture),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var total))
                {
                    throw new InvalidOperationException("the count statement did not return a number");
                }

                var fromCache = rows.FromCache || count.FromCache;
                DateTimeOffset? cachedAt = null;
                if (rows.FromCache) cachedAt = rows.CachedAt;
                if (count.FromCache && (cachedAt is null || (count.CachedAt is not null && count.CachedAt > cachedAt)))
                {
                    cachedAt = count.CachedAt;
                }

                sw.Stop();
                var truncated = (long)(page - 1) * limit + rows.Rows.Count < total;
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.Browse,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Success,
                    Query = string.IsNullOrEmpty(plan.WhereText) ? tableName : tableName + " " + plan.WhereText,
                    ResultCount = (int)Math.Min(total, int.MaxValue),
                    DurationMs = (int)sw.ElapsedMilliseconds,
                    FromCache = fromCache,
                }, ct);
                return Results.Ok(new BrowseResponse(
                    rows.Columns,
                    rows.Rows,
                    truncated,
                    total,
                    fromCache,
                    cachedAt));
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogWarning(ex, "Browse failed for {Id}", id);
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.Browse,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Error,
                    Query = tableName,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                }, ct);
                return Results.Problem(statusCode: 502, title: "browse failed", detail: ex.Message);
            }
        });

        group.MapPost("/{id}/query", async (
            string id,
            HttpContext httpContext,
            DatabaseQueryRequest req,
            IDatabaseConnectionStore store,
            IDatabaseReadService readService,
            IAuditLogger auditLogger,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return ConnectionNotFoundResult.Create(id, httpContext);
            if (row.Provider == DatabaseProvider.Redis)
            {
                return Results.BadRequest(RedisRequirement.NotSql(row, "use /keys"));
            }

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

            var maxRows = req.MaxRows;
            if (maxRows < 1) maxRows = 1;
            if (maxRows > 1000) maxRows = 1000;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await readService.QueryAsync(row, req.Database, req.Sql, maxRows, ct, preferCache: !req.Refresh);
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
                    DurationMs = (int)sw.ElapsedMilliseconds,
                    FromCache = result.FromCache
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
            HttpContext httpContext,
            DatabaseMutateRequest req,
            IDatabaseConnectionStore store,
            IDatabaseReadCache readCache,
            ILoggerFactory loggerFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Database");
            var row = await store.FindAsync(id, ct);
            if (row is null) return ConnectionNotFoundResult.Create(id, httpContext);
            if (row.Provider == DatabaseProvider.Redis)
            {
                return Results.BadRequest(RedisRequirement.NotSql(row, "mutations are not supported"));
            }

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
                adapter = DatabaseAdapterFactory.CreateAdapter(DatabaseConnectionStringFactory.Target(row, req.Database));
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
                readCache.InvalidateConnection(id);
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
        DatabaseConnectionStringFactory.IsServerLevel(c));

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

    internal sealed record AdapterResult(
        IDatabaseAdapter? Adapter,
        IResult? Error);

    /// <summary>
    /// Creates an adapter for an already-resolved connection, translating the
    /// provider's "not supported" failure into a 400. Returns the adapter or
    /// an error result; never both.
    /// </summary>
    private static AdapterResult TryCreateAdapter(
        DatabaseConnection connection, ILogger logger)
    {
        try
        {
            return new AdapterResult(DatabaseAdapterFactory.CreateAdapter(connection), null);
        }
        catch (NotSupportedException ex)
        {
            logger.LogWarning(ex, "Database adapter not supported");
            return new AdapterResult(null, Results.BadRequest(ex.Message));
        }
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

    /// <summary>
    /// Tests a Redis connection: reach the server, refuse a topology this phase
    /// does not support, then ping one logical database.
    ///
    /// <para>
    /// Both test routes - the one that takes a form and the one that takes a
    /// stored id - asked the same questions in their own copy, so the two could
    /// answer differently after a change to either. They ask here now.
    /// </para>
    /// </summary>
    private static async Task<DatabaseTestResponse> TestRedisAsync(
        IRedisConnectionPool pool,
        DatabaseConnection connection,
        ILogger logger,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var mux = await pool.GetAsync(connection, ct);
            var server = mux.GetEndPoints().Length > 0 ? mux.GetServer(mux.GetEndPoints()[0]) : null;
            if (server is not null)
            {
                var topology = server.ServerType;
                if (topology == ServerType.Cluster || topology == ServerType.Sentinel)
                {
                    return new DatabaseTestResponse(false, RedisRequirement.StandaloneOnly, 0);
                }
            }

            await mux.GetDatabase(0).PingAsync();
            sw.Stop();
            return new DatabaseTestResponse(true, "connected", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "Redis test failed");
            return new DatabaseTestResponse(false, "connection failed", 0);
        }
    }
}
