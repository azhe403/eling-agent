using Eling.Backend.Agent.Services;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Endpoints;

public static class AgentHostEndpoints
{
    public static WebApplication MapAgentHostEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Agent.Host");

        var host = app.MapGroup("/api/agent/host");
        host.MapGet("/drives", (HostBrowseService service) =>
        {
            try
            {
                return Results.Ok(service.ListDrives());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Host drives listing failed");
                return Results.Problem(statusCode: 500, title: "Host drives listing failed", detail: ex.Message);
            }
        });
        host.MapGet("/browse", (HostBrowseService service, string path) =>
        {
            try
            {
                return Results.Ok(service.Browse(path));
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Host browse failed for {Path}", path);
                return Results.Problem(statusCode: 500, title: "Host browse failed", detail: ex.Message);
            }
        });

        return app;
    }
}
