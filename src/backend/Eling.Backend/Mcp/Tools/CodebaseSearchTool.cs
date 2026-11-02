using System.ComponentModel;
using System.Diagnostics;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Codebase;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Search the codebase index (FTS Porter+trigram, BM25 ranking).
/// Returns path + line range + snippet ready to inject. Scope "all" (the
/// default) federates over every indexed workspace, closed ones included
/// (read-only, missing indexes skipped), and each hit carries its root so the
/// caller can tell projects apart; "project" reads this workspace only.
/// Never throws on a missing index — returns empty hits with a hint to run
/// codebase_index.
/// Each completed search records one <c>codebase_search</c> access entry with
/// the query text and the hit count; file contents never enter the trail
/// (design §3: metadata only).
/// </summary>
[McpServerToolType]
public sealed class CodebaseSearchTool
{
    private readonly CodebaseIndexService _svc;
    private readonly RuntimeRegistry? _registry;
    private readonly CodebaseAuditRecorder _audit;
    private readonly ILogger<CodebaseSearchTool>? _logger;

    public CodebaseSearchTool(
        CodebaseIndexService svc,
        RuntimeRegistry? registry = null,
        ILogger<CodebaseSearchTool>? logger = null,
        IAuditLogger? audit = null)
    {
        _svc = svc;
        _registry = registry;
        _audit = CodebaseAuditRecorder.Create(audit);
        _logger = logger;
    }

    [McpServerTool(Name = "codebase_search"), Description("FIRST move for locating code — call this before any host-native grep/glob/shell search, which cost more tokens and more round-trips for the same answer. Ranks the FTS index (Porter+trigram, BM25) and returns, per hit, path + line range + snippet ready to inject, so one call usually replaces grep -> list files -> read several files -> grep again. Searches GLOBALLY by default: every indexed workspace, closed ones included, so a hit from another project tells you which root it came from. Narrow the search instead of widening the term: pathPrefix/filePattern for a folder or file type inside a workspace, projectFilter for a workspace by folder name, scope 'project' for this workspace only, and projects for exact roots. Query the concept ('tool registration', 'server instructions'), not only the exact identifier; then read just the returned line ranges. Returns empty hits with a hint when no index exists — that is the only reason to fall back to native search.")]
    public async Task<CodebaseSearchResponse> SearchAsync(
        [Description("Search query: words or a short concept from the code, e.g. 'tool registration' or 'AddElingMcpTools'. FTS matches stems, so 'recall' also finds 'recalling'. Prefer concept terms over long verbatim strings.")] string query,
        [Description("Max hits (1-50, default 10).")] int limit = 10,
        [Description("Filter by path prefix, e.g. src/backend — use this to narrow instead of adding more query terms.")] string? pathPrefix = null,
        [Description("Glob-ish file filter, e.g. *.cs or *.md.")] string? filePattern = null,
        [Description("Narrow the search by workspace folder name, e.g. 'eling-agent'. Case-insensitive substring of the root's last segment; use it to keep a global search to one or two workspaces without knowing their absolute paths.")] string? projectFilter = null,
        [Description("Scope: 'all' (default, every indexed workspace) or 'project' (this workspace only, do this to keep a search local).")] string scope = "all",
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
            else if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase))
            {
                // The narrow case. Explicitly asked for, so the index of one
                // workspace is read directly instead of being federated.
                searchedRoots = null;
                hits = (await _svc.SearchAsync(query, clamped, prefix, filePattern, cancellationToken))
                    .Select(h => new ScopedCodebaseHit(_svc.ProjectRoot, h.Path, h.StartLine, h.EndLine, h.Content, h.Score))
                    .ToList();
            }
            else
            {
                // Global by default: every indexed workspace, not only the
                // ones a live runtime happens to cover. A codebase index is a
                // read-only file in the global store, so a closed workspace's
                // index is still fully readable — the same reasoning
                // AliveOrRegisteredAsync documents. Searching only Alive()
                // would make a "global" default silently miss closed projects.
                var roots = await SearchableRootsAsync(cancellationToken);
                roots = ApplyProjectFilter(roots, projectFilter);
                if (roots.Count == 0)
                {
                    // The filter excluded everything — report that plainly
                    // rather than returning a misleading "no hits".
                    sw.Stop();
                    var reason = $"No indexed workspace matches projectFilter '{projectFilter}'.";
                    await _audit.RecordSearchAsync(query, 0, _svc.ProjectRoot, (int)sw.ElapsedMilliseconds);
                    return new CodebaseSearchResponse([], new CodebaseSearchStats(0, sw.ElapsedMilliseconds, false, reason));
                }
                searchedRoots = roots;
                hits = await _svc.SearchAllAsync(query, clamped, roots, prefix, filePattern, cancellationToken);
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
            await _audit.RecordSearchAsync(query, filtered.Count, _svc.ProjectRoot, (int)sw.ElapsedMilliseconds);
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
            await _audit.RecordSearchAsync(query, 0, _svc.ProjectRoot, (int)sw.ElapsedMilliseconds, AuditOutcome.Error);
            return new CodebaseSearchResponse([], new CodebaseSearchStats(0, sw.ElapsedMilliseconds, false, ex.Message));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogError(ex, "Codebase search failed for query {Query} (scope={Scope}).", query, scope);
            await _audit.RecordSearchAsync(query, 0, _svc.ProjectRoot, (int)sw.ElapsedMilliseconds, AuditOutcome.Error);
            return new CodebaseSearchResponse([], new CodebaseSearchStats(0, sw.ElapsedMilliseconds, false, "Search failed or index missing. Run codebase_index first."));
        }
    }

    /// <summary>
    /// Every workspace root the global default should read: the live ones plus
    /// the registered-but-closed ones, de-duplicated and always including this
    /// project first so ties favor local code.
    /// </summary>
    private async Task<IReadOnlyList<string>> SearchableRootsAsync(CancellationToken ct)
    {
        if (_registry is null)
            return [_svc.ProjectRoot];

        // AliveOrRegisteredAsync already drops the excluded/never-indexed roots
        // and gives live processes precedence over remembered rows.
        var all = await _registry.AliveOrRegisteredAsync(ct).ConfigureAwait(false);
        var roots = all
            .Where(r => r.CodebaseEnabled && File.Exists(ElingPaths.ResolveCodebaseDbPath(r.CodebaseRoot())))
            .Select(r => r.CodebaseRoot())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!roots.Any(r => string.Equals(Path.GetFullPath(r), _svc.ProjectRoot, StringComparison.OrdinalIgnoreCase)))
            roots.Insert(0, _svc.ProjectRoot);
        return roots;
    }

    /// <summary>
    /// Narrows a global search to workspaces whose folder name contains
    /// <paramref name="filter"/> (case-insensitive). A null/blank filter keeps
    /// every root, and the current project always survives so a filter typo
    /// never hides the workspace the caller is standing in.
    /// </summary>
    private IReadOnlyList<string> ApplyProjectFilter(IReadOnlyList<string> roots, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return roots;

        var needle = filter.Trim();
        var kept = roots
            .Where(r => string.Equals(Path.GetFullPath(r), _svc.ProjectRoot, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(Path.TrimEndingDirectorySeparator(r))
                            .Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Only the current project survived: the filter matched nothing else,
        // which is worth saying out loud instead of searching anyway.
        return kept.Count == 1
               && string.Equals(Path.GetFullPath(kept[0]), _svc.ProjectRoot, StringComparison.OrdinalIgnoreCase)
               && !Path.GetFileName(Path.TrimEndingDirectorySeparator(_svc.ProjectRoot))
                   .Contains(needle, StringComparison.OrdinalIgnoreCase)
            ? []
            : kept;
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
