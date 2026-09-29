using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Core.Codebase;

public sealed class CodebaseIndexService
{
    private static readonly string[] SkipDirs = [".git", ".eling", "bin", "obj", "node_modules", ".next", "dist", "build", "out", ".turbo", ".vercel", ".artifacts"];
    private const long MaxBytes = 512 * 1024;

    private readonly string _projectRoot;
    private readonly ICodebaseIndex _index;
    private readonly ILogger<CodebaseIndexService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Set when a directory could not be enumerated, which makes the pass in
    /// flight's file list incomplete. Written and read only under
    /// <see cref="_gate"/>, so no further synchronisation is needed.
    /// </summary>
    private bool _enumerationIncomplete;

    public CodebaseIndexService(
        string projectRoot,
        ICodebaseIndex index,
        ILogger<CodebaseIndexService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _projectRoot = Path.GetFullPath(projectRoot);
        _index = index;
        _logger = logger ?? NullLogger<CodebaseIndexService>.Instance;
    }

    public string ProjectRoot => _projectRoot;

    /// <summary>
    /// <see cref="CodebaseIndexResult.Status"/> value returned instead of
    /// waiting when a pass is already in flight.
    /// </summary>
    public const string AlreadyRunningStatus = "already_indexing";

    /// <summary>
    /// True for workspace-relative paths the indexer never covers: anything
    /// under an excluded directory (mirrors the full-pass <c>SkipDirs</c>
    /// set) or outside the root. Single home so the file watcher and the
    /// indexer agree on what "indexable" means.
    /// </summary>
    public static bool IsSkippedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return true;
        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[0] == "..") return true;
        return segments.Any(s => SkipDirs.Contains(s, StringComparer.Ordinal));
    }

    /// <summary>
    /// Distinct, full-path project roots for a federated read. Falls back to
    /// this project when the caller passes nothing usable. Explicit roots are
    /// honored exactly as given — this project is not auto-added, otherwise
    /// selecting a sibling project would leak its counts into the result.
    /// </summary>
    private List<string> NormalizeRoots(IEnumerable<string>? projectRoots) =>
        (projectRoots ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() is { Count: > 0 } roots ? roots : [_projectRoot];

    public async Task<CodebaseStats> GetStatsAsync(CancellationToken ct = default)
    {
        return await _index.GetStatsAsync(ct);
    }

    /// <summary>
    /// Aggregated stats over the given project roots, or this project when null
    /// — feeds the dashboard tiles so they follow the selected scope. Sums file
    /// and chunk counts, takes the newest <c>LastIndexedAt</c>, and returns the
    /// roots that actually contributed (missing/unreadable indexes are skipped,
    /// matching <see cref="GetRecentFilesAsync"/>).
    /// </summary>
    public async Task<CodebaseScopedStats> GetScopedStatsAsync(
        IEnumerable<string>? projectRoots = null,
        CancellationToken ct = default)
    {
        var roots = NormalizeRoots(projectRoots);

        var files = 0;
        var chunks = 0;
        string? newest = null;
        var counted = new List<string>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                CodebaseStats rootStats;
                if (string.Equals(root, _projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    rootStats = await _index.GetStatsAsync(ct);
                }
                else
                {
                    var dbPath = Scope.ElingPaths.ResolveCodebaseDbPath(root);
                    if (!File.Exists(dbPath)) continue;
                    using var index = new SqliteCodebaseIndex(dbPath, readOnly: true);
                    rootStats = await index.GetStatsAsync(ct);
                }

                files += rootStats.FileCount;
                chunks += rootStats.ChunkCount;
                counted.Add(root);
                if (!string.IsNullOrWhiteSpace(rootStats.LastIndexedAt)
                    && (newest is null || string.CompareOrdinal(rootStats.LastIndexedAt, newest) > 0))
                {
                    newest = rootStats.LastIndexedAt;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stale, locked, or corrupt sibling index: skip, never throw —
                // but say so, or a dead sibling reads as an empty one.
                // Cancellation is excluded on purpose: these calls take the
                // caller's token, so swallowing it would keep walking every
                // remaining root after the caller already asked to stop.
                _logger.LogWarning(ex, "Skipped unreadable sibling index at {Root}.", root);
                continue;
            }
        }

        return new CodebaseScopedStats(files, chunks, newest, counted);
    }

    public async Task<CodebaseIndexResult> IndexAsync(
        bool full = false,
        IReadOnlyList<string>? pathPrefixes = null,
        CancellationToken ct = default)
    {
        // Single-flight, non-blocking. WaitAsync(ct) here would park a second
        // caller for as long as the first pass runs — on a large workspace that
        // reads as a hung request with no way to tell. WaitAsync(0) turns it
        // away so the caller can report "already indexing" immediately.
        if (!await _gate.WaitAsync(0, ct))
        {
            return new CodebaseIndexResult(
                0, 0, 0, 0,
                Scope.ElingPaths.ResolveCodebaseDbPath(_projectRoot),
                AlreadyRunningStatus);
        }

        try
        {
            await _index.EnsureCreatedAsync(ct);
            var scopes = NormalizeScopes(pathPrefixes);
            var ignores = GitignoreFilter.Load(_projectRoot, _logger);
            _enumerationIncomplete = false;
            var files = EnumerateFiles(scopes, ignores);
            int fc = 0, cc = 0, skipped = 0, deleted = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fullPath in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(_projectRoot, fullPath).Replace('\\', '/');
                seen.Add(rel);
                var info = new FileInfo(fullPath);
                if (info.Length > MaxBytes || IsBinary(fullPath)) { skipped++; continue; }
                var content = await File.ReadAllTextAsync(fullPath, ct);
                var hash = Hash(content);
                var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
                if (!full)
                {
                    var existing = await _index.GetFileAsync(rel, ct);
                    if (existing is not null && existing.Hash == hash && existing.MtimeUnix == mtime) { skipped++; continue; }
                }
                var chunks = CodebaseChunker.Chunk(rel, content);
                await _index.ReplaceChunksAsync(rel, hash, mtime, info.Length, chunks, ct);
                fc++; cc += chunks.Count;
            }

            // Sweep stale rows: only when indexing everything, or scoped to the same prefixes.
            // Gitignored rows are always purged: ignored content must not stay searchable.
            var indexed = await _index.ListPathsAsync(ct);
            foreach (var dbPath in indexed)
            {
                if (seen.Contains(dbPath)) continue;
                if (ignores.IsIgnored(dbPath))
                {
                    await _index.DeleteFileAsync(dbPath, ct);
                    deleted++;
                    continue;
                }
                if (scopes is not null && !InScope(dbPath, scopes)) continue;
                var absolute = Path.GetFullPath(Path.Combine(_projectRoot, dbPath.Replace('/', Path.DirectorySeparatorChar)));
                // Deleting a row is only safe once the file is proven gone.
                // Being in the index but absent from this pass is not proof:
                // a directory that failed to enumerate leaves its files out of
                // `seen` while they are still on disk, and dropping their rows
                // silently deletes indexed content. Keep the row instead — a
                // stale row is cleaned up by the next clean pass, a wrongly
                // deleted one only comes back by re-indexing from scratch.
                if (File.Exists(absolute)) continue;
                if (_enumerationIncomplete) continue;
                await _index.DeleteFileAsync(dbPath, ct);
                deleted++;
            }

            return new CodebaseIndexResult(fc, cc, skipped, deleted, _index.DbPath);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Hits a relaxation pass must reach before the next, looser pass is worth
    /// running.
    /// </summary>
    private const int MinHits = 3;

    /// <summary>
    /// Searches this project's index. Relaxation walks the spec's ladder
    /// (§4.4) in order — Porter AND, Porter OR, trigram AND, trigram OR —
    /// stopping at the first pass that clears the threshold. Precision is spent
    /// before typo tolerance: reaching for trigram early lets fuzzy hits
    /// displace exact ones, and because the trigram AND pass scores generously
    /// it can clear the threshold on its own and skip the Porter OR pass
    /// entirely.
    /// </summary>
    public async Task<IReadOnlyList<CodebaseHit>> SearchAsync(
        string query,
        int limit = 20,
        string? pathPrefix = null,
        string? filePattern = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<CodebaseHit>();
        var tokens = query.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('"', '\'', '*', '(', ')')).Where(t => t.Length > 0).ToArray();
        if (tokens.Length == 0) return Array.Empty<CodebaseHit>();

        string And(IEnumerable<string> ts) => string.Join(" AND ", ts.Select(t => $"\"{t}\""));
        string Or(IEnumerable<string> ts) => string.Join(" OR ", ts.Select(t => $"\"{t}\""));

        // A limit below the threshold could never satisfy it, so every query
        // would run all four passes to return a row or two.
        var minHits = Math.Min(MinHits, Math.Max(limit, 1));

        var porterAnd = await _index.SearchPorterAsync(And(tokens), limit, pathPrefix, filePattern, ct);
        if (porterAnd.Count >= minHits) return porterAnd;

        var porter = Merge(porterAnd, await _index.SearchPorterAsync(Or(tokens), limit, pathPrefix, filePattern, ct));
        if (porter.Count >= minHits) return porter.Take(limit).ToList();

        var withTrigram = Merge(porter, await _index.SearchTrigramAsync(And(tokens), limit, pathPrefix, filePattern, ct));
        if (withTrigram.Count >= minHits) return withTrigram.Take(limit).ToList();

        var trigramOr = await _index.SearchTrigramAsync(Or(tokens), limit, pathPrefix, filePattern, ct);
        return Merge(withTrigram, trigramOr).Take(limit).ToList();
    }

    /// <summary>
    /// Federated search over this project plus the given sibling project roots
    /// ("all scope"). Each sibling DB is opened read-only: roots without an
    /// existing index file are skipped, per-project failures never fail the
    /// whole search, and every hit carries its owning project root.
    /// Local hits get a small locality bias so ties favor this project.
    /// </summary>
    public async Task<IReadOnlyList<ScopedCodebaseHit>> SearchAllAsync(
        string query,
        int limit = 20,
        IEnumerable<string>? projectRoots = null,
        string? pathPrefix = null,
        string? filePattern = null,
        CancellationToken ct = default)
    {
        // Explicit roots are searched exactly as given — the caller's project
        // is NOT auto-added, otherwise selecting project B would also leak
        // hits from the current project. Callers that want "everything"
        // ("all" scope) must include the current root themselves.
        var roots = NormalizeRoots(projectRoots);

        var all = new List<ScopedCodebaseHit>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var local = string.Equals(root, _projectRoot, StringComparison.OrdinalIgnoreCase);
                IReadOnlyList<CodebaseHit> hits;
                if (local)
                {
                    hits = await SearchAsync(query, limit, pathPrefix, filePattern, ct);
                }
                else
                {
                    var dbPath = Scope.ElingPaths.ResolveCodebaseDbPath(root);
                    if (!File.Exists(dbPath)) continue;
                    using var index = new SqliteCodebaseIndex(dbPath, readOnly: true);
                    var svc = new CodebaseIndexService(root, index, _logger);
                    hits = await svc.SearchAsync(query, limit, pathPrefix, filePattern, ct);
                }
                var bias = local ? 1.1 : 1.0;
                all.AddRange(hits.Select(h => new ScopedCodebaseHit(root, h.Path, h.StartLine, h.EndLine, h.Content, h.Score * bias)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stale, locked, or corrupt sibling index: skip, never throw —
                // but say so, or that project's hits silently vanish.
                // Cancellation is excluded on purpose: these calls take the
                // caller's token, so swallowing it would keep walking every
                // remaining root after the caller already asked to stop.
                _logger.LogWarning(ex, "Sibling search failed for {Root}; its hits are absent from this result.", root);
                continue;
            }
        }
        return all.OrderByDescending(h => h.Score).Take(Math.Min(Math.Max(limit, 1), 50)).ToList();
    }

    /// <summary>
    /// Most recently indexed files across the given project roots (or this
    /// project when null) — feeds the empty-search-box listing. Read-only,
    /// never throws for missing indexes.
    /// </summary>
    public async Task<IReadOnlyList<CodebaseFileSummary>> GetRecentFilesAsync(
        int limit = 20,
        IEnumerable<string>? projectRoots = null,
        CancellationToken ct = default)
    {
        var roots = NormalizeRoots(projectRoots);

        var all = new List<CodebaseFileSummary>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.Equals(root, _projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    all.AddRange((await _index.ListRecentFilesAsync(limit, ct))
                        .Select(f => new CodebaseFileSummary(root, f.Path, f.ChunkCount, f.LastIndexedAt)));
                }
                else
                {
                    var dbPath = Scope.ElingPaths.ResolveCodebaseDbPath(root);
                    if (!File.Exists(dbPath)) continue;
                    using var index = new SqliteCodebaseIndex(dbPath, readOnly: true);
                    all.AddRange((await index.ListRecentFilesAsync(limit, ct))
                        .Select(f => new CodebaseFileSummary(root, f.Path, f.ChunkCount, f.LastIndexedAt)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stale, locked, or corrupt sibling index: skip, never throw —
                // but say so, or a dead sibling reads as an empty one.
                // Cancellation is excluded on purpose: these calls take the
                // caller's token, so swallowing it would keep walking every
                // remaining root after the caller already asked to stop.
                _logger.LogWarning(ex, "Skipped unreadable sibling index at {Root} while listing recent files.", root);
                continue;
            }
        }
        return all.OrderByDescending(f => f.LastIndexedAt, StringComparer.Ordinal).Take(Math.Min(Math.Max(limit, 1), 100)).ToList();
    }

    private static IReadOnlyList<CodebaseHit> Merge(IReadOnlyList<CodebaseHit> a, IReadOnlyList<CodebaseHit> b)
    {
        var byKey = new Dictionary<string, CodebaseHit>();
        foreach (var h in a.Concat(b))
        {
            var k = $"{h.Path}:{h.StartLine}";
            if (!byKey.TryGetValue(k, out var cur) || h.Score > cur.Score) byKey[k] = h;
        }
        return byKey.Values.OrderByDescending(h => h.Score).ToList();
    }

    private IEnumerable<string> EnumerateFiles(IReadOnlyList<string>? scopes, GitignoreFilter ignores)
    {
        var roots = new List<string>();
        if (scopes is null || scopes.Count == 0)
        {
            roots.Add(_projectRoot);
        }
        else
        {
            foreach (var scope in scopes)
            {
                var absolute = Path.GetFullPath(Path.Combine(_projectRoot, scope.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(absolute))
                {
                    var rel = Path.GetRelativePath(_projectRoot, absolute).Replace('\\', '/');
                    if (!ignores.IsIgnored(rel)) yield return absolute;
                }
                else if (Directory.Exists(absolute)) roots.Add(absolute);
            }
            if (roots.Count == 0) yield break;
        }

        var q = new Queue<string>(roots);
        while (q.Count > 0)
        {
            var dir = q.Dequeue();
            string[] subs = [], files = [];
            try { subs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch (Exception ex)
            {
                // A directory that cannot be enumerated still yields a partial
                // file list, and the caller sweeps unseen rows — so this must be
                // loud, and must mark the pass incomplete: a silent skip here is
                // what turns into deleted index rows.
                _logger.LogWarning(ex, "Could not enumerate {Dir}; its files are missing from this pass.", dir);
                _enumerationIncomplete = true;
                continue;
            }
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(_projectRoot, f).Replace('\\', '/');
                if (!ignores.IsIgnored(rel)) yield return f;
            }
            foreach (var s in subs)
            {
                if (SkipDirs.Contains(Path.GetFileName(s))) continue;
                var rel = Path.GetRelativePath(_projectRoot, s).Replace('\\', '/');
                if (ignores.IsIgnored(rel)) continue;
                q.Enqueue(s);
            }
        }
    }

    private static IReadOnlyList<string>? NormalizeScopes(IReadOnlyList<string>? prefixes)
    {
        if (prefixes is null || prefixes.Count == 0) return null;
        var cleaned = prefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/').Trim().Trim('/', '.'))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return cleaned.Count == 0 ? null : cleaned;
    }

    private static bool InScope(string relPath, IReadOnlyList<string> scopes)
        => scopes.Any(s => relPath.Equals(s, StringComparison.Ordinal) || relPath.StartsWith(s + "/", StringComparison.Ordinal));

    private bool IsBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[4096];
            var n = fs.Read(buf, 0, buf.Length);
            for (var i = 0; i < n; i++) if (buf[i] == 0) return true;
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Every way File.OpenRead can refuse — a locked file, a denied ACL, a
            // path holding characters the platform rejects, an I/O error — means
            // the same thing here: the file is not indexed. The catch stays wide
            // on purpose. Narrowing it to IOException and UnauthorizedAccessException
            // let ArgumentException escape and abort the whole pass, taking the
            // stale sweep down with it. The exclusion filter is kept for symmetry
            // with the cancellation-aware catches, not because this call can
            // throw one: File.OpenRead is synchronous and takes no token.
            _logger.LogDebug(ex, "Could not read {Path} to detect binary content; skipping it.", path);
            return true;
        }
    }

    private static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
