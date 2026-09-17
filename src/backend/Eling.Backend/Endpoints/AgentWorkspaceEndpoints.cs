using Eling.Backend.Agent.Services;
using Eling.Backend.Dtos;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Endpoints;

public static class AgentWorkspaceEndpoints
{
    public static WebApplication MapAgentWorkspaceEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Agent.Workspaces");

        var workspaces = app.MapGroup("/api/agent/workspaces");
        workspaces.MapGet("/", (WorkspaceRegistry registry) =>
            TypedResults.Ok(new WorkspaceListResponse([.. registry.List()])));
        workspaces.MapPost("/", (WorkspaceRegistry registry, AddWorkspaceRequest req) =>
        {
            try
            {
                registry.Add(req.Path);
                return Results.Ok(new WorkspaceListResponse([.. registry.List()]));
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Invalid workspace path {Path}", req.Path);
                return Results.BadRequest(ex.Message);
            }
        });
        workspaces.MapDelete("/", (WorkspaceRegistry registry, string path) =>
        {
            registry.Remove(path);
            return Results.Ok(new WorkspaceListResponse([.. registry.List()]));
        });

        var files = app.MapGroup("/api/agent/files");
        files.MapGet("/list", (WorkspaceRegistry registry, BackendFileTools tools, string root, string? path) =>
        {
            try
            {
                var dir = registry.Resolve(root, path);
                return Results.Ok(new FileListResponse([.. tools.ListDir(dir)]));
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Workspace escape attempt root={Root} path={Path}", root, path);
                return Results.StatusCode(403);
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
        });
        files.MapGet("/read", (WorkspaceRegistry registry, BackendFileTools tools, string root, string? path) =>
        {
            try
            {
                var full = registry.Resolve(root, path);
                var (content, truncated) = tools.ReadText(full);
                return Results.Ok(new FileReadResponse(content, truncated));
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Workspace escape attempt root={Root} path={Path}", root, path);
                return Results.StatusCode(403);
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
            catch (InvalidOperationException ex) when (ex.Message == "binary")
            {
                return Results.StatusCode(415);
            }
        });
        files.MapPost("/write", (WorkspaceRegistry registry, BackendFileTools tools, WriteFileRequest req) =>
        {
            try
            {
                var full = registry.Resolve(req.Root, req.Path);
                tools.WriteText(full, req.Content);
                return Results.Ok();
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Workspace escape attempt root={Root} path={Path}", req.Root, req.Path);
                return Results.StatusCode(403);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        return app;
    }
}
