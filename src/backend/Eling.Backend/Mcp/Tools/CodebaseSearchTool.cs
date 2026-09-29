using System.ComponentModel;
using System.Diagnostics;
using Eling.Backend.Dtos;
using Eling.Core.Codebase;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Search the codebase index (FTS Porter+trigram, BM25 ranking).
/// Returns path + line range + snippet ready to inject. Scope "project"
/// searches this project only; "all" federates over every alive runtime
/// project (read-only, missing indexes skipped). Never throws on a
/// missing index — returns empty hits with a hint to run codebase_index.
/// </summary>
[McpServerToolType]
public sealed class CodebaseSearchTool
{
    private readonly CodebaseIndexService _svc;
    private readonly RuntimeRegistry? _registry;
    private readonly ILogger<CodebaseSearchTool>? _logger;

    public CodebaseSearchTool(
        CodebaseIndexService svc,
        RuntimeRegistry? registry = null,
        ILogger<CodebaseSearchTool>? logger = null)
    {
        _svc = svc;
        _registry = registry;
        _logger = logger;
    }

    [McpServerTool(Name = "codebase_search"), Description("Search the codebase index (FTS Porter+trigram, BM25 ranking). Returns path + line range + snippet ready to inject. Scope 'project' (default) searches this workspace only; 'all' searches every alive workspace; 'projects' searches an explicit list of workspace roots and overrides scope.")]
    public async Task<CodebaseSearchResponse> SearchAsync(
        [Description("Search query.")] string query,
        [Description("Max hits (1-50, default 10).")] int limit = 10,
        [Description("Filter by path prefix, e.g. src/backend.")] string? pathPrefix = null,
        [Description("Glob-ish file filter, e.g. *.cs or *.md.")] string? filePattern = null,
        [Description("Scope: 'project' (default, this workspace) or 'all' (every alive workspace).")] string scope = "project",
        [Description("Explicit workspace roots to search, e.g. [\"C:/Proj/A\"]. Overrides scope when non-empty.")] string[]? projects = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var clamped = Math.Min(Math.Max(limit, 1), 50);
        if (string.IsNullOrWhiteSpace(query))
        {
            sw.Stop();
            return new CodebaseSearchResponse([], new CodebaseSearchStats(0, sw.ElapsedMilliseconds, false, "Query is empty."));
        }

        try
        {
            var prefix = string.IsNullOrWhiteSpace(pathPrefix) ? null : pathPrefix;
            // Model-supplied roots get the same validation as the REST
            // endpoints: a tool argument must not be a way around the
            // exclusion gate the registry enforces.
            var explicitRoots = ElingPaths.EnsureCodebaseRootsAllowed(projects ?? []).ToList();
            IReadOnlyList<string>? searchedRoots = null;
            IReadOnlyList<ScopedCodebaseHit> hits;
            if (explicitRoots.Count > 0)
            {
                searchedRoots = explicitRoots;
                hits = await _svc.SearchAllAsync(query, clamped, explicitRoots, prefix, filePattern, cancellationToken);
            }
            else if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
            {
                var roots = _registry?.Alive().Where(r => r.CodebaseEnabled).Select(r => r.CodebaseRoot()).ToList()
                    ?? [_svc.ProjectRoot];
                if (!roots.Any(r => string.Equals(Path.GetFullPath(r), _svc.ProjectRoot, StringComparison.OrdinalIgnoreCase)))
                    roots.Insert(0, _svc.ProjectRoot);
                searchedRoots = roots;
                hits = await _svc.SearchAllAsync(query, clamped, roots, prefix, filePattern, cancellationToken);
            }
            else
            {
                hits = (await _svc.SearchAsync(query, clamped, prefix, filePattern, cancellationToken))
                    .Select(h => new ScopedCodebaseHit(_svc.ProjectRoot, h.Path, h.StartLine, h.EndLine, h.Content, h.Score))
                    .ToList();
            }
            // filePattern rides down into SQL, so the limit is already taken
            // from the filtered set — trimming here again would only hide rows.
            var filtered = hits.ToList();
            sw.Stop();
            // An empty hit list means "no match" only when an index actually
            // exists to match against. Reporting fresh=true with no index on
            // disk would have the caller conclude the code is absent, so
            // derive it from the DB's presence.
            var fresh = HasIndexOnDisk(searchedRoots, _svc.ProjectRoot);
            var hint = filtered.Count == 0
                ? fresh
                    ? "No hits matched the query and filters."
                    : "No index for this scope. Run codebase_index to build it."
                : null;
            return new CodebaseSearchResponse(
                filtered.Select(h => new CodebaseSearchHit(h.ProjectRoot, h.Path, h.StartLine, h.EndLine, h.Content, h.Score)).ToList(),
                new CodebaseSearchStats(filtered.Count, sw.ElapsedMilliseconds, fresh, hint));
        }
        catch (ArgumentException ex)
        {
            // Scope validation rejected a root. A tool has no status code to
            // return, so hand the reason back in the response — the caller can
            // correct its own arguments instead of retrying blindly.
            sw.Stop();
            _logger?.LogWarning("Rejected codebase search scope: {Reason}", ex.Message);
            return new CodebaseSearchResponse([], new CodebaseSearchStats(0, sw.ElapsedMilliseconds, false, ex.Message));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogError(ex, "Codebase search failed for query {Query} (scope={Scope}).", query, scope);
            return new CodebaseSearchResponse([], new CodebaseSearchStats(0, sw.ElapsedMilliseconds, false, "Search failed or index missing. Run codebase_index first."));
        }
    }

    /// <summary>
    /// True when at least one index in the searched scope exists on disk. Null
    /// roots mean this project's own index.
    /// </summary>
    private static bool HasIndexOnDisk(IReadOnlyList<string>? roots, string localRoot)
    {
        if (roots is null) return File.Exists(ElingPaths.ResolveCodebaseDbPath(localRoot));
        return roots.Any(r => File.Exists(ElingPaths.ResolveCodebaseDbPath(r)));
    }
}
