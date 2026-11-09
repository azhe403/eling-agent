using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Endpoints;

public static class DatabaseKeyEndpoints
{
    public static WebApplication MapDatabaseKeyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/databases");

        group.MapGet("/{id}/keys", async (
            string id,
            IDatabaseConnectionStore store,
            IRedisKeyReadService readService,
            IAuditLogger auditLogger,
            int database = 0,
            string? prefix = null,
            long cursor = 0,
            int pageOffset = 0,
            int pageSize = 200,
            CancellationToken ct = default) =>
        {
            var gate = await RedisKeyRoute.OpenAsync(id, store, ct);
            if (gate.Http is not null) return gate.Http;
            if (RedisKeyRoute.RefuseIndex(database, out var dbIndex) is { } refusal) return refusal;
            prefix ??= string.Empty;
            if (prefix.Length > 1024) return Results.BadRequest("prefix too long");
            pageSize = Math.Clamp(pageSize, 1, 500);

            return await RedisKeyRoute.RunAsync(
                gate.Row!,
                DatabaseAuditActions.Keys,
                prefix,
                auditLogger,
                ct,
                async () =>
                {
                    var page = await readService.ScanAsync(gate.Row!, dbIndex, prefix, cursor, pageOffset, pageSize, ct);
                    return (object?)page;
                });
        });

        group.MapGet("/{id}/keys/databases", async (
            string id,
            IDatabaseConnectionStore store,
            IRedisKeyReadService readService,
            IAuditLogger auditLogger,
            CancellationToken ct = default) =>
        {
            var gate = await RedisKeyRoute.OpenAsync(id, store, ct);
            if (gate.Http is not null) return gate.Http;

            var view = await RedisKeyRoute.RunAsync(
                gate.Row!,
                DatabaseAuditActions.RedisDatabases,
                string.Empty,
                auditLogger,
                ct,
                async () =>
                {
                    var databases = await readService.ListDatabasesAsync(gate.Row!, ct);
                    return databases
                        .Select(db => new RedisDatabaseInfoDto(db.Index, db.Keys, db.FromCache, db.CachedAt))
                        .ToList();
                });
            return view;
        });

        group.MapGet("/{id}/keys/inspect", async (
            string id,
            IDatabaseConnectionStore store,
            IRedisKeyReadService readService,
            IAuditLogger auditLogger,
            string key,
            int database = 0,
            CancellationToken ct = default) =>
        {
            var gate = await RedisKeyRoute.OpenAsync(id, store, ct);
            if (gate.Http is not null) return gate.Http;
            if (string.IsNullOrEmpty(key)) return Results.BadRequest("key must not be blank");
            if (RedisKeyRoute.RefuseIndex(database, out var dbIndex) is { } refusal) return refusal;

            return await RedisKeyRoute.RunAsync(
                gate.Row!,
                DatabaseAuditActions.InspectKey,
                key,
                auditLogger,
                ct,
                async () =>
                {
                    var result = await readService.InspectAsync(gate.Row!, dbIndex, key, ct);
                    return (object?)result;
                });
        });

        group.MapDelete("/{id}/keys/delete", async (
            string id,
            IDatabaseConnectionStore store,
            IRedisKeyReadService readService,
            IAuditLogger auditLogger,
            string key,
            int database = 0,
            CancellationToken ct = default) =>
        {
            var gate = await RedisKeyRoute.OpenAsync(id, store, ct);
            if (gate.Http is not null) return gate.Http;
            if (string.IsNullOrEmpty(key)) return Results.BadRequest("key must not be blank");
            if (RedisKeyRoute.RefuseIndex(database, out var dbIndex) is { } refusal) return refusal;

            return await RedisKeyRoute.RunAsync(
                gate.Row!,
                DatabaseAuditActions.RedisDelete,
                key,
                auditLogger,
                ct,
                async () =>
                {
                    var result = await readService.DeleteKeyAsync(gate.Row!, dbIndex, key, ct);
                    return (object?)new RedisKeyDeleteDto(result.Deleted);
                });
        });

        group.MapGet("/{id}/keys/preview", async (
            string id,
            IDatabaseConnectionStore store,
            IRedisKeyReadService readService,
            IAuditLogger auditLogger,
            string prefix,
            int database = 0,
            CancellationToken ct = default) =>
        {
            var gate = await RedisKeyRoute.OpenAsync(id, store, ct);
            if (gate.Http is not null) return gate.Http;
            if (string.IsNullOrEmpty(prefix)) return Results.BadRequest("prefix must not be blank");
            if (RedisKeyRoute.RefuseIndex(database, out var dbIndex) is { } refusal) return refusal;

            return await RedisKeyRoute.RunAsync(
                gate.Row!,
                DatabaseAuditActions.RedisPrefixPreview,
                prefix,
                auditLogger,
                ct,
                async () =>
                {
                    var result = await readService.PreviewPrefixAsync(gate.Row!, dbIndex, prefix, ct);
                    return (object?)new RedisPrefixPreviewDto(
                        result.Prefix,
                        result.Keys.ToArray(),
                        result.Total,
                        result.Truncated);
                },
                resultCountOf: answer => answer is RedisPrefixPreviewDto dto ? (int)dto.Total : null);
        });

        group.MapDelete("/{id}/keys/prefix", async (
            string id,
            IDatabaseConnectionStore store,
            IRedisKeyReadService readService,
            IAuditLogger auditLogger,
            string prefix,
            int database = 0,
            CancellationToken ct = default) =>
        {
            var gate = await RedisKeyRoute.OpenAsync(id, store, ct);
            if (gate.Http is not null) return gate.Http;
            if (string.IsNullOrEmpty(prefix)) return Results.BadRequest("prefix must not be blank");
            if (RedisKeyRoute.RefuseIndex(database, out var dbIndex) is { } refusal) return refusal;

            return await RedisKeyRoute.RunAsync(
                gate.Row!,
                DatabaseAuditActions.RedisPrefixDelete,
                prefix,
                auditLogger,
                ct,
                async () =>
                {
                    var result = await readService.DeletePrefixAsync(gate.Row!, dbIndex, prefix, ct);
                    return (object?)new RedisPrefixDeleteDto(result.Deleted, result.Truncated);
                },
                resultCountOf: answer => answer is RedisPrefixDeleteDto dto ? (int)dto.Deleted : null);
        });

        group.MapPost("/{id}/keys/command", async (
            string id,
            RedisCommandRequest req,
            IDatabaseConnectionStore store,
            IRedisKeyReadService readService,
            IRedisReadCache redisReadCache,
            IAuditLogger auditLogger,
            CancellationToken ct = default) =>
        {
            var gate = await RedisKeyRoute.OpenAsync(id, store, ct);
            if (gate.Http is not null) return gate.Http;

            var command = req?.Command ?? string.Empty;
            var args = req?.Args ?? new List<string>();
            if (RedisKeyRoute.RefuseIndex(req?.Database ?? 0, out var dbIndex) is { } refusal) return refusal;

            var classification = RedisCommandSafety.Classify(command, args);
            if (classification.Verdict != RedisVerdict.Allowed)
            {
                await auditLogger.RecordAsync(new AuditEvent
                {
                    Trail = AuditTrail.Access,
                    Category = AuditCategory.Database,
                    Action = DatabaseAuditActions.RedisCommand,
                    Actor = "dashboard",
                    Target = id,
                    Outcome = AuditOutcome.Denied,
                    Query = command + " " + string.Join(" ", args),
                    DurationMs = 0
                }, ct);
                return Results.BadRequest(new { error = classification.Reason });
            }

            var result = await RedisKeyRoute.RunAsync(
                gate.Row!,
                DatabaseAuditActions.RedisCommand,
                command + " " + string.Join(" ", args),
                auditLogger,
                ct,
                async () =>
                {
                    var answer = await readService.ExecuteCommandAsync(gate.Row!, dbIndex, command, args, ct);
                    return (object?)answer;
                });

            // The command may have changed the key's type, TTL or value, so the
            // cached inspection for its operand no longer describes the key.
            if (args.Count > 0) redisReadCache.RemoveKey(id, dbIndex, args[0]);

            return result;
        });

        return app;
    }
}
