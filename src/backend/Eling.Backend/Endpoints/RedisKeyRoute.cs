using Eling.Backend.Bootstrap;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Endpoints;

/// <summary>
/// The four Redis key routes each repeated the same find-and-gate block and
/// the same catch ladder. That block lives here now; each route keeps only
/// what is its own — which service method to call and which audit action to
/// record.
///
/// The catch ladder maps the exceptions the read service can raise onto the
/// HTTP answers they deserve:
///
/// - 404 - the key is not there -
/// - 400 - a policy refusal (non-Redis, out-of-range db, cluster) -
/// - 502 - the server refused the connection's credentials (NOAUTH /
///   WRONGPASS), which means "our configuration is wrong", not "the
///   database is down" -
/// - 503 - anything else that came out of Redis.
/// </summary>
public static class RedisKeyRoute
{
    /// <summary>
    /// Fetch the connection row and answer the two pre-conditions every key
    /// route asks: does the id exist, and is it a Redis connection?
    /// </summary>
    public static async Task<Result> OpenAsync(string id, IDatabaseConnectionStore store, HttpContext httpContext, CancellationToken ct)
    {
        var row = await store.FindAsync(id, ct);
        if (row is null)
        {
            httpContext.Items[ApiEnvelopeMiddleware.ErrorCodeItem] = "connection_not_found";
            return Result.NotFound(id);
        }
        if (row.Provider != DatabaseProvider.Redis)
        {
            return Result.BadRequest(RedisRequirement.NotRedis(row, "key browsing"));
        }
        return Result.Ok(row);
    }

    /// <summary>
    /// The one refusal every route gives a database index outside 0-15. Null
    /// when the index is valid; the caller keeps the parsed value via <c>out</c>.
    /// </summary>
    public static IResult? RefuseIndex(int database, out RedisDatabaseIndex index) =>
        RedisDatabaseIndex.TryCreate(database, out index)
            ? null
            : Results.BadRequest(RedisDatabaseIndex.Refusal);

    /// <summary>
    /// Run one key operation: time it, record it, and map the exceptions the
    /// read service raises onto the HTTP answers above.
    /// </summary>
    public static async Task<IResult> RunAsync(
        DatabaseConnection row,
        string action,
        string query,
        IAuditLogger auditLogger,
        CancellationToken ct,
        Func<Task<object?>> work,
        Func<object?, int?>? resultCountOf = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var answer = await work();
            sw.Stop();
            await auditLogger.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = action,
                Actor = "dashboard",
                Target = row.Id,
                Outcome = AuditOutcome.Success,
                Query = query,
                ResultCount = resultCountOf?.Invoke(answer),
                DurationMs = (int)sw.ElapsedMilliseconds
            }, ct);
            return Results.Ok(answer);
        }
        catch (KeyNotFoundException ex)
        {
            sw.Stop();
            return Results.NotFound(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            sw.Stop();
            return Results.BadRequest(ex.Message);
        }
        catch (Exception ex) when (ex.IsRedisCredentialFailure())
        {
            sw.Stop();
            return Results.Problem(statusCode: 502, title: "redis credentials rejected", detail: ex.Message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Results.Problem(statusCode: 503, title: "redis unavailable", detail: ex.Message);
        }
    }

    /// <summary>
    /// One of the two pre-conditions, or the connection row when both held.
    /// </summary>
    public sealed class Result
    {
        public IResult? Http { get; private init; }
        public DatabaseConnection? Row { get; private init; }
        public static Result Ok(DatabaseConnection row) => new() { Row = row };
        public static Result NotFound(string id) => new() { Http = Results.NotFound(new { error = $"Connection '{id}' is gone - it may have been deleted in another tab or by another eling app; the connection list is being refreshed." }) };
        public static Result BadRequest(string detail) => new() { Http = Results.BadRequest(detail) };
    }
}
