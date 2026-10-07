using Eling.Core;
using Eling.Core.Runtime;

namespace Eling.Backend;

public static class CoordinatorEndpoints
{
    public static void MapCoordinatorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/coordinator");

        // Loopback-only binding is the access control; no host filter needed
        // (the old RequireHost("localhost") rejected 127.0.0.1 callers).

        group.MapPost("/register", (RuntimeRegistration registration, RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster) =>
        {
            registry.Register(registration);
            broadcaster.Notify("runtimes");
            return Results.Ok();
        });

        group.MapPost("/heartbeat/{pid:int}", (int pid, RuntimeRegistry registry) =>
            registry.Heartbeat(pid) ? Results.Ok() : Results.NotFound());

        group.MapDelete("/unregister/{pid:int}", (int pid, RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster) =>
        {
            if (registry.Unregister(pid))
            {
                broadcaster.Notify("runtimes");
                return Results.NoContent();
            }
            return Results.NotFound();
        });

        // GET /runtimes moved to CoordinatorController: it now serves two modes
        // (live-only by default, live-or-registered on request), and adding a
        // query parameter to a minimal-API route is group-wide here — one
        // misinferred parameter takes down every route in the group.

        group.MapPost("/notify-change", (MemoryChangeBroadcaster broadcaster) =>
        {
            broadcaster.Notify("coordinator");
            return Results.Ok();
        });
    }
}
