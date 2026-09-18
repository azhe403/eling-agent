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
        // "runtimes" events to connected subscribers.
        app.Services.GetService<RuntimeDirWatcher>()?.Start();

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
        app.MapFallbackToFile("index.html");
    }

    private static void MapSseEvents(this WebApplication app)
    {
        app.MapGet("/api/events/memories", async (HttpContext context, MemoryChangeBroadcaster broadcaster) =>
        {
            var ct = context.RequestAborted;
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-transform";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            await context.Response.WriteAsync("data: connected\n\n", ct);
            await context.Response.Body.FlushAsync(ct);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var subscribeTask = Task.Run(async () =>
            {
                await foreach (var evt in broadcaster.SubscribeAsync(ct))
                {
                    await context.Response.WriteAsync($"data: {evt}\n\n", ct);
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
        });
    }
}
