using System.Runtime.CompilerServices;
using System.Text.Json;
using Eling.Backend.Codebase;
using Eling.Backend.Dtos;
using Eling.Backend.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
        // codebase dir ("codebase" events on any index rebuild).
        app.Services.GetService<RuntimeDirWatcher>()?.Start();
        app.Services.GetService<CodebaseDirWatcher>()?.Start();

        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", pid = Environment.ProcessId }));
        app.MapSseEvents();
        app.MapCoordinatorEndpoints();
        app.MapMemoryRoutes();
        app.MapScopedMemoryRoutes();
        app.MapMemoryMaintenanceEndpoints();
        app.MapAgentEndpoints();
        app.MapAgentWorkspaceEndpoints();
        app.MapAgentHostEndpoints();
        app.MapAgentChatEndpoints();
        app.MapCodebaseRoutes();
        app.MapControllers();
        app.MapFallbackToFile("index.html");
    }

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
    }

    // SSE frames must stay on a single line: the event format terminates a
    // frame at a newline, and clients parse the `data:` payload as JSON
    // directly. The shared defaults are indented because they back on-disk
    // payloads, so the stream gets its own compact instance.
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
    /// keep proxies from closing an idle connection. Returns as soon as either
    /// the source or the ping loop ends.
    /// </summary>
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

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var item in source)
            {
                await context.Response.WriteAsync($"data: {serialize(item)}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }, ct);

        var pingTask = Task.Run(async () =>
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await context.Response.WriteAsync(": ping\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }, ct);

        await Task.WhenAny(subscribeTask, pingTask);
    }
}
