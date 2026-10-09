using Eling.Backend.Codebase;
using Eling.Backend.Dtos;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Endpoints;

public static class CodebaseEndpoints
{
    public static WebApplication MapCodebaseRoutes(this WebApplication app)
    {
        app.MapGroup("/api/codebase").MapCodebaseEndpoints();
        return app;
    }

    private static RouteGroupBuilder MapCodebaseEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/status", GetStatusAsync);
        group.MapGet("/search", SearchAsync);
        group.MapPost("/rebuild-index", RebuildIndexAsync);
        group.MapGet("/rebuild-index/{jobId}", GetRebuildIndexAsync);
        return group;
    }

    private static async Task<IResult> GetStatusAsync(CodebaseIndexService svc, Bootstrap.CodebaseWatcherService watcher, RuntimeRegistry registry, ILoggerFactory loggerFactory, string? scope = null, string[]? project = null, CancellationToken cancellationToken = default)
    {
        var logger = loggerFactory.CreateLogger("Eling.Backend.Endpoints.CodebaseEndpoints");
        try
        {
            var resolved = await ResolveScopeAsync(svc, registry, scope, project, cancellationToken);
            var scoped = await svc.GetScopedStatsAsync(resolved.Roots, cancellationToken);

            // files/chunks/lastIndexedAt describe the selected scope and
            // roots lists the workspaces those numbers belong to.
            // When a single project root is targeted, dbPath points directly to its index DB.
            var targetRoot = resolved.Roots?.Count == 1 ? resolved.Roots[0] : (resolved.Roots is null ? svc.ProjectRoot : null);
            var resolvedDbPath = targetRoot is not null
                ? (string.Equals(targetRoot, svc.ProjectRoot, StringComparison.OrdinalIgnoreCase)
                    ? svc.DbPath
                    : ElingPaths.ResolveCodebaseDbPath(targetRoot))
                : svc.DbPath;

            return TypedResults.Ok(new
            {
                scope = resolved.EffectiveScope,
                projectCount = scoped.Roots.Count,
                roots = scoped.Roots,
                files = scoped.Files,
                chunks = scoped.Chunks,
                lastIndexedAt = scoped.LastIndexedAt,
                watcherActive = watcher.IsActive,
                dbPath = resolvedDbPath
            });
        }
        catch (ArgumentException ex)
        {
            // Scope validation rejected a root (relative path, or excluded).
            // The message names the offender, so pass it through instead of
            // hiding it behind a generic failure.
            logger.LogWarning("Rejected codebase status scope: {Reason}", ex.Message);
            return TypedResults.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read codebase status.");
            return TypedResults.BadRequest("Failed to read codebase status.");
        }
    }

    /// <summary>
    /// The resolved scope for one codebase read: the effective scope name
    /// plus the concrete workspace roots a federated read should cover.
    /// Null roots means "this workspace only".
    /// </summary>
    internal sealed record ResolvedScope(
        string EffectiveScope,
        List<string>? Roots);

    /// <summary>
    /// Maps the request's scope selector onto the concrete project roots a
    /// federated read should cover. Null roots means "this project only".
    /// </summary>
    /// <remarks>
    /// Internal rather than private so <c>CodebaseFileController</c> resolves
    /// scope exactly the way these endpoints do — two copies of this rule
    /// would drift, and the viewer has to read the index the list was drawn
    /// from.
    /// </remarks>
    internal static async Task<ResolvedScope> ResolveScopeAsync(
        CodebaseIndexService svc,
        RuntimeRegistry registry,
        string? scope,
        string[]? project,
        CancellationToken cancellationToken = default)
    {
        // Explicit roots come straight from the query string, so they are the
        // one place a caller can name an arbitrary directory. Validate before
        // they reach the indexer or a federated read — see
        // ElingPaths.EnsureCodebaseRootsAllowed for why the absolute-path and
        // exclusion checks both apply.
        var explicitRoots = ElingPaths.EnsureCodebaseRootsAllowed(project ?? []);
        if (explicitRoots.Count > 0)
        {
            return new ResolvedScope("projects", explicitRoots.ToList());
        }

        if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
        {
            // Every workspace that has an index, not only the ones with a live
            // process. A codebase index is a read-only file in the global store
            // and stays fully readable after its process exits, so "all" that
            // skipped closed projects undercounted by exactly the projects a
            // user is most likely to want back — and made the total read lower
            // than a single project's own tiles, which is incoherent.
            //
            // CodebaseEnabled is the registry's own exclusion decision
            // (RuntimeSelfRegistration applies IsCodebaseExcluded at
            // registration). Federating without the filter would resurrect
            // every excluded runtime the gate exists to keep out.
            var roots = (await registry.AliveOrRegisteredAsync(cancellationToken))
                .Where(r => r.CodebaseEnabled)
                .Select(r => r.CodebaseRoot())
                .ToList();
            if (!roots.Any(r => string.Equals(Path.GetFullPath(r), svc.ProjectRoot, StringComparison.OrdinalIgnoreCase)))
                roots.Insert(0, svc.ProjectRoot);
            return new ResolvedScope("all", roots);
        }

        return new ResolvedScope(scope ?? "project", null);
    }

    /// <summary>
    /// Turns the same scope selector the read endpoints use into a rebuild
    /// request. An explicitly named project is always indexed — the DB file is
    /// created on demand. Only an "all projects" sweep skips roots that have
    /// no index yet, because that scope means "refresh what is already
    /// indexed", and the DB filename is a one-way hash so an unindexed root
    /// cannot even be discovered from the store.
    /// </summary>
    internal static async Task<CodebaseRebuildRequest> ResolveRebuildRequestAsync(
        CodebaseIndexService svc,
        RuntimeRegistry registry,
        string? scope,
        string[]? project,
        bool full,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveScopeAsync(svc, registry, scope, project, cancellationToken);
        var candidates = resolved.Roots ?? [svc.ProjectRoot];

        var targets = new List<string>();
        var skipped = new List<string>();
        foreach (var root in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var indexed = File.Exists(ElingPaths.ResolveCodebaseDbPath(root));
            if (indexed || !string.Equals(resolved.EffectiveScope, "all", StringComparison.Ordinal))
                targets.Add(root);
            else
                skipped.Add(root);
        }

        return new CodebaseRebuildRequest(resolved.EffectiveScope, targets, skipped, full);
    }

    private static async Task<IResult> SearchAsync(CodebaseIndexService svc, RuntimeRegistry registry, ILoggerFactory loggerFactory, string? q, int? limit, string? pathPrefix = null, string? scope = null, string[]? project = null, CancellationToken cancellationToken = default)
    {
        var logger = loggerFactory.CreateLogger("Eling.Backend.Endpoints.CodebaseEndpoints");
        if (limit is < 1 or > 50)
            limit = 20;
        try
        {
            var resolved = await ResolveScopeAsync(svc, registry, scope, project, cancellationToken);
            var roots = resolved.Roots;
            var effectiveScope = resolved.EffectiveScope;

            // Empty query = top mode: most recently indexed files, same scope.
            if (string.IsNullOrWhiteSpace(q))
            {
                var files = await svc.GetRecentFilesAsync(limit ?? 20, roots);
                return TypedResults.Ok(new { query = q, limit = limit, scope = effectiveScope, mode = "top", results = files.Select(f => new { f.ProjectRoot, f.Path, f.ChunkCount, f.LastIndexedAt }) });
            }

            IReadOnlyList<ScopedCodebaseHit> hits;
            if (roots is not null)
            {
                hits = await svc.SearchAllAsync(q, limit ?? 20, roots, pathPrefix);
            }
            else
            {
                hits = (await svc.SearchAsync(q, limit ?? 20, pathPrefix))
                    .Select(h => new ScopedCodebaseHit(svc.ProjectRoot, h.Path, h.StartLine, h.EndLine, h.Content, h.Score))
                    .ToList();
            }
            return TypedResults.Ok(new { query = q, limit = limit, scope = effectiveScope, mode = "search", results = hits.Select(h => new { h.ProjectRoot, h.Path, h.StartLine, h.EndLine, h.Content, h.Score }) });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Rejected codebase search scope: {Reason}", ex.Message);
            return TypedResults.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Codebase search failed for query {Query} (scope={Scope}).", q, scope);
            return TypedResults.BadRequest("Search failed.");
        }
    }

    /// <summary>
    /// Starts a rebuild over the selected scope and returns immediately with
    /// the job snapshot; progress follows on the rebuild SSE channel. Calling
    /// it without parameters keeps the original behaviour: this backend's own
    /// workspace, incremental.
    /// </summary>
    private static async Task<IResult> RebuildIndexAsync(
        CodebaseIndexService svc,
        CodebaseRebuildJobRunner runner,
        RuntimeRegistry registry,
        ILoggerFactory loggerFactory,
        [FromQuery] string? scope = null,
        [FromQuery] string[]? project = null,
        [FromQuery] bool full = false,
        CancellationToken cancellationToken = default)
    {
        var logger = loggerFactory.CreateLogger("Eling.Backend.Endpoints.CodebaseEndpoints");
        try
        {
            var request = await ResolveRebuildRequestAsync(svc, registry, scope, project, full, cancellationToken);
            var snapshot = runner.Start(request);
            if (request.Skipped.Count > 0)
            {
                logger.LogInformation(
                    "Codebase rebuild skipped {Skipped} project(s) with no index yet.",
                    request.Skipped.Count);
            }
            return TypedResults.Accepted($"/api/codebase/rebuild-index/{snapshot.JobId}", snapshot);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Rejected codebase rebuild scope: {Reason}", ex.Message);
            return TypedResults.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Codebase index rebuild failed to start (scope={Scope}).", scope);
            return TypedResults.BadRequest("Rebuild failed.");
        }
    }

    private static IResult GetRebuildIndexAsync(string jobId, CodebaseRebuildJobRunner runner)
    {
        var snapshot = runner.Get(jobId);
        return snapshot is null
            ? TypedResults.NotFound("Unknown rebuild job.")
            : TypedResults.Ok(snapshot);
    }
}
