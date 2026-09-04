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
        app.UseRouting();

        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", pid = Environment.ProcessId }));
        app.MapSseEvents();
        app.MapCoordinatorEndpoints();
        app.MapMemoryRoutes();
        app.MapScopedMemoryRoutes();
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
