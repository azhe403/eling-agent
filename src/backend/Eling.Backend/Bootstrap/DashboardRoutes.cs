using System.Runtime.CompilerServices;
using System.Text.Json;
using Eling.Backend.Codebase;
using Eling.Backend.Dtos;
using Eling.Backend.Endpoints;
using Eling.Backend.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Wires the static-file pipeline + REST endpoints (health, SSE, coordinator,
/// memory routes, fallback to dashboard UI). Only called when this instance
/// actually owns the dashboard port.
/// </summary>
public static class DashboardRoutes
{
    public static void Map(WebApplication app)
    {
        // The envelope middleware runs first: every endpoint below it — static
        // files, the SPA fallback, the correlation middleware, routing — is
        // guaranteed a non-empty JSON body on errors and a uniform success shape.
        app.UseMiddleware<ApiEnvelopeMiddleware>();

        // Order matters: defaults/static BEFORE routing, MapFallbackToFile LAST
        // so deep links reach the SPA fallback instead of being captured earlier.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.Use(async (context, next) =>
        {
            var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
                ?? context.Request.Headers["X-Request-ID"].FirstOrDefault()
                ?? ("http-" + Guid.NewGuid().ToString("N")[..8]);

            context.Response.Headers["X-Correlation-ID"] = correlationId;

            using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
            {
                await next(context);
            }
        });

        app.UseRouting();

        // Registered only in owner mode (see DashboardServices); start watching
        // the shared runtime dir so cross-process membership changes push SSE
        // "runtimes" events to connected subscribers. Same for the shared
        // codebase dir ("codebase" events on any index rebuild) and the shared
        // database registry ("databases" events on any connection change).
        app.Services.GetService<RuntimeDirWatcher>()?.Start();
        app.Services.GetService<CodebaseDirWatcher>()?.Start();
        app.Services.GetService<AuditDirWatcher>()?.Start();
        app.Services.GetService<DatabaseRegistryWatcher>()?.Start();

        app.MapGet("/health", () => Results.Ok(HealthPayload()));
        // The same probe under /api, because that is the only prefix the
        // dashboard's dev proxy forwards to the backend - a dashboard read of
        // "/health" would hit the frontend server in dev and never reach
        // the API. One handler, two paths, so the two cannot drift.
        app.MapGet("/api/health", () => Results.Ok(HealthPayload()));
        app.MapSseEvents();
        app.MapCommitLeaseEndpoints();
        app.MapCoordinatorEndpoints();
        app.MapMemoryRoutes();
        app.MapScopedMemoryRoutes();
        app.MapStatsRoutes();
        app.MapMemoryMaintenanceEndpoints();
        app.MapJudgeEndpoints();
        app.MapToolsEndpoints();
        app.MapShellEndpoints();
        app.MapLogsEndpoints();
        app.MapAgentEndpoints();
        app.MapAgentWorkspaceEndpoints();
        app.MapAgentHostEndpoints();
        app.MapAgentChatEndpoints();
        app.MapCodebaseRoutes();
        app.MapDatabaseEndpoints();
        app.MapDatabaseKeyEndpoints();
        app.MapAuditRoutes();
        app.MapControllers();
        app.MapFallbackToFile("index.html");
    }

    /// <summary>
    /// The liveness payload both health routes answer with.
    /// </summary>
    private static object HealthPayload() => new { status = "Healthy", pid = Environment.ProcessId };

    internal static void MapSseEvents(this WebApplication app)
    {
        // One topic-name stream carries every notification, and more than one page
        // consumes it: the memory page, the codebase page and the create page all
        // subscribe here. Topics are therefore NOT filtered server-side. A filter
        // here would silently kill the live updates of whichever page wanted the
        // topic it drops — dropping "codebase" stopped the codebase page's tiles
        // and result list from refreshing at all. Each consumer picks its own
        // topics in use-memories-sse.ts instead.
        app.MapGet("/api/events/memories", (HttpContext context, MemoryChangeBroadcaster broadcaster) =>
        {
            var ct = context.RequestAborted;
            return WriteSseAsync(context, broadcaster.SubscribeAsync(ct), item => item, ct);
        });

        // Rebuild progress is a typed payload, so it rides its own channel
        // instead of widening the topic-name stream the memory UI already uses.
        app.MapGet("/api/events/codebase-rebuild", (HttpContext context, CodebaseRebuildBroadcaster broadcaster) =>
        {
            var ct = context.RequestAborted;
            return WriteSseAsync(context, broadcaster.SubscribeAsync(ct), SerializeRebuildProgress, ct);
        });

        // Runtime metrics ride the same typed-channel pattern: volatile
        // payloads on a 2s pump, not change events on the topic stream, so
        // the lifecycle stream's contract stays untouched.
        app.MapGet("/api/events/runtime-metrics", (HttpContext context, RuntimeMetricsBroadcaster broadcaster) =>
        {
            var ct = context.RequestAborted;
            return WriteSseAsync(context, broadcaster.SubscribeAsync(ct), item => JsonSerializer.Serialize(item, SseJsonOptions), ct);
        });
    }

    // SSE frames must stay on a single line: the event format terminates a
    // frame at a newline, and clients parse the `data:` payload as JSON
    // directly. The shared defaults are indented because they back on-disk
    // payloads, so the stream gets its own compact instance.
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// The commit-lease surface of the dashboard owner: the snapshot the panel
    /// renders, the push channel it subscribes to, and the snapshot route is
    /// completed by <c>CommitLeaseController</c> (active + force-release).
    /// Only this host maps them — a peer owns no dashboard and answers none of
    /// these routes, exactly as for the memory SSE channel.
    /// </summary>
    internal static void MapCommitLeaseEndpoints(this WebApplication app)
    {
        // The snapshot is what the client (re)renders after every frame, so a
        // reconnecting client resyncs from the stream itself; the REST GET
        // stays for the first paint and for explicit refreshes.
        app.MapGet("/api/events/commit-lease", (HttpContext context, CommitLeaseBroadcaster broadcaster) =>
        {
            var ct = context.RequestAborted;
            return WriteSseAsync(context, broadcaster.SubscribeAsync(ct), item => JsonSerializer.Serialize(item, SseJsonOptions), ct);
        });
    }

    private static string SerializeRebuildProgress(CodebaseRebuildProgress snapshot) =>
        JsonSerializer.Serialize(snapshot, SseJsonOptions);

    /// <summary>
    /// Forwards only the topics in <paramref name="topics"/>.
    /// </summary>
    /// <remarks>
    /// Used by the typed rebuild-progress channel, where the payload is a
    /// serialized object rather than a topic name. Not used on
    /// <c>/api/events/memories</c>: that stream has several consumers with
    /// different interests, so filtering there takes away another page's updates.
    /// </remarks>
    private static async IAsyncEnumerable<string> FilterTopics(
        IAsyncEnumerable<string> source,
        HashSet<string> topics,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var topic in source.WithCancellation(ct))
        {
            if (topics.Contains(topic))
            {
                yield return topic;
            }
        }
    }

    /// <summary>
    /// Writes one <c>text/event-stream</c> response: an initial
    /// <c>data: connected</c> frame so clients know the stream is live, every
    /// source item as a <c>data:</c> frame, and a comment ping every 15s to
    /// keep proxies from closing an idle connection. A single loop awaits
    /// either the source or the ping timer, so every frame is pushed and
    /// flushed in the order it happens instead of waiting behind the ping.
    /// Returns when the source completes or the client disconnects.
    /// </summary>
    /// <param name="source">The async source of items to forward.</param>
    /// <param name="serialize">Turns an item into one <c>data:</c> frame.</param>
    /// <param name="ct">Cancels the loop when the client disconnects.</param>
    private static async Task WriteSseAsync<T>(
        HttpContext context,
        IAsyncEnumerable<T> source,
        Func<T, string> serialize,
        CancellationToken ct)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        await context.Response.WriteAsync("data: connected\n\n", ct);
        await context.Response.Body.FlushAsync(ct);

        // Single writer, see the method doc: one loop awaits either the source
        // or the ping timer so frames flush in order at arrival time.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        // Not `await using`: the finally below owns disposal with the
        // NotSupportedException guard. A generated disposal at method exit
        // would run unguarded after the explicit one and re-throw the same
        // exception, tearing the stream the guard is meant to protect.
        var enumerator = source.GetAsyncEnumerator(ct);
        var itemTask = enumerator.MoveNextAsync().AsTask();
        var pingTask = timer.WaitForNextTickAsync(ct).AsTask();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(itemTask, pingTask);
                if (completed == itemTask)
                {
                    bool advanced;
                    try
                    {
                        advanced = await itemTask;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!advanced) break;

                    await context.Response.WriteAsync($"data: {serialize(enumerator.Current)}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                    itemTask = enumerator.MoveNextAsync().AsTask();
                }
                else
                {
                    // The client may go away between the ping firing and the write:
                    // treat any failure here as the client leaving and stop quietly.
                    try
                    {
                        await context.Response.WriteAsync(": ping\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                    }
                    catch (Exception) when (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    pingTask = timer.WaitForNextTickAsync(ct).AsTask();
                }
            }
        }
        finally
        {
            var logger = context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(nameof(DashboardRoutes))
                ?? NullLogger.Instance;

            // The exception fires when a MoveNextAsync is still in flight at
            // DisposeAsync time (the common case on client disconnect); the
            // compiler-generated async-iterator enumerator does not support
            // DisposeAsync then, which tore the stream and logged a bogus
            // error. That is expected here, so swallow it deliberately.
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (NotSupportedException ex)
            {
                // Expected: a MoveNextAsync was still pending when the client went away; the
                // async-iterator enumerator does not support DisposeAsync in that state.
                // Log at Debug so it is not noise, but never swallow silently - the convention
                // requires a log entry per catch.
                logger.LogDebug(ex, "SSE enumerator does not support DisposeAsync (client went away with a MoveNextAsync in flight); swallowed deliberately.");
            }

            // If the loop broke out via the ping/cancel path, the pending
            // MoveNextAsync may never be awaited; observe its fault whenever
            // it completes so it does not surface as unobserved. Client
            // disconnects fault it with OperationCanceledException, which is
            // expected and stays quiet; anything else is a real source fault
            // and gets a log line.
            _ = itemTask.ContinueWith(
                t =>
                {
                    if (t.Exception?.InnerException is not OperationCanceledException)
                    {
                        logger.LogDebug(t.Exception, "SSE source MoveNextAsync faulted after the response loop ended; observed to keep it from surfacing as unobserved.");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
