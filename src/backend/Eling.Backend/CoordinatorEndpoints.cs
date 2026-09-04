using Eling.Core;

namespace Eling.Backend;

public static class CoordinatorEndpoints
{
    public static void MapCoordinatorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/coordinator");

        // Loopback-only binding is the access control; no host filter needed
        // (the old RequireHost("localhost") rejected 127.0.0.1 callers).

        group.MapPost("/register", (RuntimeRegistration registration, RuntimeRegistry registry) =>
        {
            registry.Register(registration);
            return Results.Ok();
        });

        group.MapPost("/heartbeat/{pid:int}", (int pid, RuntimeRegistry registry) =>
            registry.Heartbeat(pid) ? Results.Ok() : Results.NotFound());

        group.MapDelete("/unregister/{pid:int}", (int pid, RuntimeRegistry registry) =>
            registry.Unregister(pid) ? Results.NoContent() : Results.NotFound());

        group.MapGet("/runtimes", (RuntimeRegistry registry) => Results.Ok(registry.Alive()));

        group.MapPost("/notify-change", (MemoryChangeBroadcaster broadcaster) =>
        {
            broadcaster.Notify("coordinator");
            return Results.Ok();
        });
    }
}
