using System.Globalization;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Eling.Core.Memory;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Endpoints;

/// <summary>
/// The audit trail's read API (design §9). Two endpoints, one per trail, over
/// the same machine-global index the writer batches into — there is no write
/// endpoint. Everything here is a read: nothing an endpoint does is itself
/// audited, which is why these handlers never record (design §12).
/// </summary>
public static class AuditEndpoints
{
    /// <summary>Rows returned when the caller names no limit.</summary>
    private const int DefaultLimit = 50;

    /// <summary>Uppermost page size. A larger window is a filter, not a page.</summary>
    private const int MaxLimit = 200;

    /// <summary>
    /// How much of the window each index round trip reads. The index query is
    /// the only place the trail is filtered on <c>scope</c> and
    /// <c>projectRoot</c> — the engine's <see cref="AuditQuery"/> has no such
    /// column, so those two filters run over the rows it returns. Over-reading
    /// by four pages is what keeps a filter that discards most of the window
    /// from returning a page nobody wanted.
    /// </summary>
    private const int FetchFactor = 4;

    /// <summary>
    /// How many matching rows one page call is allowed to gather. The read loop
    /// stops here rather than at <see cref="DefaultLimit"/>, so a page that has
    /// to over-read to satisfy a filter still knows when to stop.
    /// </summary>
    private const int MaxGathered = MaxLimit * FetchFactor;

    public static WebApplication MapAuditRoutes(this WebApplication app)
    {
        var audit = app.MapGroup("/api/audit");
        audit.MapGet("/changes", ListChangesAsync);
        audit.MapGet("/access", ListAccessAsync);
        return app;
    }

    private static Task<IResult> ListChangesAsync(
        IAuditIndex index,
        string? actor,
        string? action,
        string? scope,
        string? projectRoot,
        string? from,
        string? to,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
        => ReadAsync(
            index,
            AuditTrail.Change,
            actor, action, scope, projectRoot, from, to, limit, cursor,
            cancellationToken);

    private static Task<IResult> ListAccessAsync(
        IAuditIndex index,
        string? actor,
        string? action,
        string? scope,
        string? projectRoot,
        string? from,
        string? to,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
        => ReadAsync(
            index,
            AuditTrail.Access,
            actor, action, scope, projectRoot, from, to, limit, cursor,
            cancellationToken);

    /// <summary>
    /// One bounded read of one trail. The request must carry a time window: an
    /// unbounded scan of a machine-global store holding hundreds of MB is
    /// exactly what design §9 forbids, so a missing <c>from</c>/<c>to</c> pair
    /// is a 400 rather than a slow answer.
    /// </summary>
    private static async Task<IResult> ReadAsync(
        IAuditIndex index,
        AuditTrail trail,
        string? actor,
        string? action,
        string? scope,
        string? projectRoot,
        string? from,
        string? to,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var window = TryBuildWindow(from, to, cursor);
        if (window is null)
        {
            return TypedResults.BadRequest(
                "Query parameters 'from' and 'to' are required: a request must be bounded by a time window "
                + "(ISO-8601 UTC, e.g. 2026-10-16T00:00:00Z).");
        }

        if (!TryParseScope(scope, out var scopeFilter))
        {
            return TypedResults.BadRequest($"Invalid scope '{scope}'.");
        }

        var pageSize = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var gathered = await GatherAsync(
            index,
            new AuditQuery(
                trail,
                window.From,
                null,
                Action: action,
                Actor: actor,
                Limit: MaxLimit),
            new AuditFilter(scopeFilter, projectRoot, window),
            pageSize,
            cancellationToken);

        var page = PageAsync(gathered, pageSize, out var nextCursor);
        return TypedResults.Ok(new AuditEventListDto(page, nextCursor, window.From, window.To, pageSize));
    }

    /// <summary>
    /// Reads the window newest-first until it holds enough matching rows to cut
    /// a page. Each round trip's upper bound is moved strictly below the oldest
    /// row it returned, so the window slides without ever re-reading a row —
    /// and without the <c>To</c> bound the caller sent being reused after the
    /// first round, which would otherwise repeat the same rows until the loop's
    /// budget ran out.
    /// </summary>
    private static async Task<IReadOnlyList<AuditEventDto>> GatherAsync(
        IAuditIndex index,
        AuditQuery seed,
        AuditFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var gathered = new List<AuditEventDto>(MaxGathered);
        var upper = filter.Window.Cursor;
        var exhausted = false;

        while (gathered.Count < MaxGathered)
        {
            var rows = await index.QueryAsync(
                seed with { From = filter.Window.From, To = upper ?? filter.Window.To, Limit = MaxLimit },
                cancellationToken);

            foreach (var row in rows)
            {
                if (!filter.Matches(row)) continue;
                gathered.Add(AuditEventDto.From(row));
                if (gathered.Count >= MaxGathered) break;
            }

            if (rows.Count == 0)
            {
                exhausted = true;
                break;
            }

            // Every row this round trip read has now been either kept or
            // discarded by the filter, so the next round starts strictly older
            // than the oldest of them.
            if (rows.Count < MaxLimit)
            {
                exhausted = true;
                break;
            }

            upper = rows[^1].Event.Timestamp;
        }

        _ = exhausted;
        return gathered;
    }

    /// <summary>
    /// Cuts one page out of the gathered rows and reports the cursor. The page
    /// is never cut through a tie: rows sharing the boundary timestamp are
    /// emitted together, because the cursor they produce excludes that whole
    /// instant on the next page — cutting mid-tie would drop the rows above the
    /// cut and never show them again.
    /// </summary>
    private static IReadOnlyCollection<AuditEventDto> PageAsync(
        IReadOnlyList<AuditEventDto> gathered,
        int pageSize,
        out string? nextCursor)
    {
        if (gathered.Count == 0)
        {
            nextCursor = null;
            return gathered;
        }

        var take = Math.Min(pageSize, gathered.Count);
        var boundary = gathered[take - 1].Timestamp;
        while (take < gathered.Count && gathered[take].Timestamp == boundary && take < MaxGathered)
        {
            take++;
        }

        nextCursor = take < gathered.Count ? boundary.ToString("O", CultureInfo.InvariantCulture) : null;
        return gathered.Take(take).ToList();
    }

    private static bool TryParseScope(string? scope, out MemoryScopeKind? kind)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            kind = null;
            return true;
        }

        if (MemoryScopeParser.TryParseScope(scope, out var parsed))
        {
            kind = parsed;
            return true;
        }

        kind = null;
        return false;
    }

    /// <summary>
    /// The request's window, with the cursor folded into it: a cursor replaces
    /// the upper bound, and the row that produced it has already been seen.
    /// </summary>
    private static AuditWindow? TryBuildWindow(string? from, string? to, string? cursor)
    {
        if (!TryParseInstant(from, out var lower) || !TryParseInstant(to, out var upper))
        {
            return null;
        }

        if (upper < lower)
        {
            return null;
        }

        DateTimeOffset? cursorAt = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryParseInstant(cursor, out var parsed) || parsed < lower)
            {
                return null;
            }

            cursorAt = parsed;
        }

        return new AuditWindow(lower, upper, cursorAt);
    }

    private static bool TryParseInstant(string? value, out DateTimeOffset instant)
    {
        // Any explicit offset is honoured and then normalized: the engine stores
        // timestamps as UTC (amendment C2), and comparing a +07:00 instant
        // against a stored "…+00:00" as text would read as the wrong side of
        // the window.
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            instant = parsed;
            return true;
        }

        instant = default;
        return false;
    }

    /// <summary>
    /// Which rows one request wants. The trail, the action and the actor are
    /// pushed into the index query; scope and project root are matched here
    /// because the engine's <see cref="AuditQuery"/> has no column for either,
    /// and the query they belong to already carries the trail.
    /// </summary>
    private sealed record AuditFilter(
        MemoryScopeKind? Scope,
        string? ProjectRoot,
        AuditWindow Window)
    {
        public bool Matches(AuditRecord row)
        {
            var e = row.Event;
            if (Window.Cursor is not null && e.Timestamp >= Window.Cursor.Value)
            {
                return false;
            }

            if (Scope is not null && e.Scope != Scope)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ProjectRoot)
                && !string.Equals(e.ProjectRoot, ProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The bounding window of one request. <see cref="Cursor"/> is the instant
    /// the previous page stopped at and, when set, supersedes <see cref="To"/>:
    /// the reader has already seen everything at or after it.
    /// </summary>
    private sealed record AuditWindow(
        DateTimeOffset From,
        DateTimeOffset To,
        DateTimeOffset? Cursor);
}
