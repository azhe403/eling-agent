using Eling.Backend.Agent.Services;
using Eling.Backend.Dtos;
using Eling.Core.Projects;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Endpoints;

public static class AgentWorkspaceEndpoints
{
    public static WebApplication MapAgentWorkspaceEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Agent.Workspaces");

        var workspaces = app.MapGroup("/api/agent/workspaces");
        workspaces.MapGet("/", async (
            IWorkspacesRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var rows = await registry.ListAsync(cancellationToken);
            return TypedResults.Ok(new WorkspaceListResponse([.. rows.Select(r => r.WorkspaceRoot)]));
        });
        workspaces.MapPost("/", async (
            IWorkspacesRegistry registry,
            AddWorkspaceRequest req,
            CancellationToken cancellationToken) =>
        {
            var root = req.Path?.Trim();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                logger.LogWarning("Invalid workspace path {Path}", req.Path);
                return Results.BadRequest($"Workspace directory does not exist: {req.Path}");
            }

            var full = Path.GetFullPath(root!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // An explicit attach is one row in the same catalogue the runtime
            // records into: the dashboard and the desktop see one list. The
            // store mints the id and both timestamps; head scope is the folder
            // itself until a registration resolves the real .eling head.
            //
            // A folder already in the catalogue is left untouched: the runtime's
            // registration owns head scope and the codebase decision, and a
            // desktop re-attach must not clobber them with placeholder values.
            var existing = await registry.FindByRootAsync(full, cancellationToken);
            if (existing is null)
            {
                registry.Record(new RegisteredWorkspace(
                    WorkspaceId: string.Empty,
                    WorkspaceRoot: full,
                    HeadScopeRoot: full,
                    CodebaseEnabled: false,
                    FirstSeenAt: DateTimeOffset.UtcNow,
                    LastSeenAt: DateTimeOffset.UtcNow));
            }

            var rows = await registry.ListAsync(cancellationToken);
            return Results.Ok(new WorkspaceListResponse([.. rows.Select(r => r.WorkspaceRoot)]));
        });
        workspaces.MapDelete("/", async (
            IWorkspacesRegistry registry,
            string path,
            CancellationToken cancellationToken) =>
        {
            await registry.DeleteByRootAsync(path, cancellationToken);
            var rows = await registry.ListAsync(cancellationToken);
            return Results.Ok(new WorkspaceListResponse([.. rows.Select(r => r.WorkspaceRoot)]));
        });

        var files = app.MapGroup("/api/agent/files");
        files.MapGet("/list", (BackendFileTools tools, string root, string? path) =>
        {
            try
            {
                var dir = ResolveWithin(root, path);
                return Results.Ok(new FileListResponse([.. tools.ListDir(dir)]));
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Workspace escape attempt root={Root} path={Path}", root, path);
                return Results.StatusCode(403);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
        });
        files.MapGet("/read", (BackendFileTools tools, string root, string? path) =>
        {
            try
            {
                var full = ResolveWithin(root, path);
                var (content, truncated) = tools.ReadText(full);
                return Results.Ok(new FileReadResponse(content, truncated));
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Workspace escape attempt root={Root} path={Path}", root, path);
                return Results.StatusCode(403);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
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
        files.MapPost("/write", (IWorkspacesRegistry registry, BackendFileTools tools, WriteFileRequest req) =>
        {
            try
            {
                var full = ResolveWithin(req.Root, req.Path);
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

    /// <summary>
    /// Containment check for workspace-scoped file tools: a relative path stays
    /// inside its workspace root, anything that escapes it is a 403, not a
    /// resolved path. The root is trimmed first so a trailing separator does not
    /// turn every child into an escape.
    /// </summary>
    private static string ResolveWithin(string root, string? relativePath)
    {
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty));
        if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Path escapes workspace root.");
        }

        return full;
    }
}
