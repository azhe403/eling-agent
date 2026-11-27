namespace Eling.Core.Docs;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Coordinates Markdown content, the global SQLite docs store, and audit.
/// Ownership, origin, and JSON metadata were removed.
/// </summary>
public sealed class DocsService
{
    private readonly IDocsIndex _docsIndex;
    private readonly IDocsAuditStore _docsAudit;
    private readonly List<string> _workspaceRoots;
    private readonly ILogger<DocsService> _logger;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public DocsService(
        IDocsIndex index,
        IDocsAuditStore audit,
        IEnumerable<string> workspaceRoots,
        ILogger<DocsService>? logger = null)
    {
        _docsIndex = index ?? throw new ArgumentNullException(nameof(index));
        _docsAudit = audit ?? throw new ArgumentNullException(nameof(audit));
        _workspaceRoots = workspaceRoots?.ToList() ?? [];
        _logger = logger ?? NullLogger<DocsService>.Instance;
    }

    public IReadOnlyList<string> WorkspaceRoots => _workspaceRoots.AsReadOnly();

    public void SetWorkspaceRoots(IEnumerable<string> roots)
    {
        _workspaceRoots.Clear();
        _workspaceRoots.AddRange(roots);
    }

    public static string ResolveWorkspaceId(string root)
    {
        var normalized = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string? NormalizeWorkspace(string? workspace)
        => string.IsNullOrWhiteSpace(workspace) || workspace.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : ResolveWorkspaceId(workspace);

    public static string ComputeHash(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    public async Task<ScanResult> ScanAsync(
        bool full = false,
        IEnumerable<string>? paths = null,
        string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        if (!await _scanGate.WaitAsync(0, ct)) return new ScanResult(0, 0, 0, 0, 0, true);
        try
        {
            var roots = workspaceRoot is not null ? [workspaceRoot] : _workspaceRoots.ToArray();
            var indexed = 0; var skipped = 0; var deleted = 0; var errors = 0;
            var messages = new List<string>();

            foreach (var root in roots)
            {
                var workspaceId = ResolveWorkspaceId(root);
                var coverage = await _docsIndex.GetPolicyAsync(ct);
                var files = paths?.Where(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList()
                            ?? EnumerateMarkdown(root, coverage).ToList();
                var coveredPaths = files.Select(file => GetRelativePath(file, root)).ToHashSet(StringComparer.Ordinal);

                foreach (var file in files)
                {
                    var path = GetRelativePath(file, root);
                    var hash = ComputeHash(await File.ReadAllTextAsync(file, ct));
                    var existing = await _docsIndex.GetByPathAsync(workspaceId, path, ct);
                    if (!full && existing is not null && existing.FileHash == hash) { skipped++; continue; }

                    try
                    {
                        var doc = await ParseAsync(file, path, workspaceId, hash, existing, ct);
                        await _docsIndex.IndexAsync(doc.Doc, doc.Sections, doc.Refs, doc.Body);
                        foreach (var relation in doc.Relations) await _docsIndex.UpsertRelationAsync(relation);
                        indexed++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        messages.Add($"Error indexing {file}: {ex.Message}");
                        _logger.LogWarning(ex, "Error indexing file {File}", file);
                    }
                }

                if (paths is null)
                {
                    foreach (var row in await _docsIndex.ListAsync(workspaceId, includeArchived: true))
                    {
                        if (coveredPaths.Contains(row.Path)) continue;
                        await _docsIndex.DeleteAsync(row.DocId);
                        deleted++;
                    }
                }
            }
            return new ScanResult(indexed, skipped, deleted, errors, roots.Length, false, messages);
        }
        finally { _scanGate.Release(); }
    }

    private static IEnumerable<string> EnumerateMarkdown(string root, CoveragePolicy coverage)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            if (IsCoveredPath(file, root, coverage)) yield return file;
        }
    }

    /// <summary>Same coverage rule the scanner uses, exposed so watchers can drop out-of-scope events.</summary>
    public static bool IsCoveredPath(string file, string root, CoveragePolicy coverage)
    {
        var relative = GetRelativePath(file, root).Replace('\\', '/');
        if (!relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        var excluded = new[] { ".git", "node_modules", "bin", "obj", ".next", ".eling" };
        if (excluded.Any(part => relative.Split('/').Contains(part, StringComparer.OrdinalIgnoreCase))) return false;
        if (relative.StartsWith(".superpowers/sdd/", StringComparison.OrdinalIgnoreCase)) return false;
        var excludes = coverage.Exclude ?? Array.Empty<string>();
        var includes = coverage.Include ?? Array.Empty<string>();
        if (excludes.Any(pattern => MatchPattern(relative, pattern))) return false;
        if (includes.Length > 0 && !includes.Any(pattern => MatchPattern(relative, pattern))) return false;
        return true;
    }

    /// <summary>Drops an indexed row whose Markdown file was removed outside Eling.</summary>
    public async Task<bool> DeleteIndexedPathAsync(string workspaceRoot, string path, CancellationToken ct = default)
    {
        var workspaceId = ResolveWorkspaceId(workspaceRoot);
        var existing = await _docsIndex.GetByPathAsync(workspaceId, GetRelativePath(path, workspaceRoot), ct);
        if (existing is null) return false;
        await _docsIndex.DeleteAsync(existing.Id.Value);
        return true;
    }

    /// <summary>
    /// Coverage patterns are globs over the workspace-relative path: <c>**</c>
    /// spans directories, <c>*</c> stays inside one segment, and <c>?</c> is one
    /// character. A pattern without a wildcard keeps the older prefix/segment
    /// meaning so existing policies do not change behaviour.
    /// </summary>
    private static bool MatchPattern(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var normalizedPath = path.Replace('\\', '/');
        var glob = pattern.Replace('\\', '/').Trim();
        if (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob[2..];
        if (glob.EndsWith('/')) glob += "**";

        var regex = new StringBuilder("^");
        var hasWildcard = false;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                hasWildcard = true;
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    regex.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    regex.Append(".*");
                    i++;
                }
            }
            else if (c == '*')
            {
                hasWildcard = true;
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                hasWildcard = true;
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }
        if (!hasWildcard) regex.Append("(?:/.*)?");
        regex.Append('$');
        return Regex.IsMatch(normalizedPath, regex.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string GetRelativePath(string file, string root)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    private async Task<DocParseResult> ParseAsync(
        string absolutePath, string relativePath, string workspaceId, string hash,
        Doc? existing, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(absolutePath, ct);
        var frontMatter = DocFrontMatterParser.Parse(content, _logger);
        var body = frontMatter.RemainingContent;
        var isNew = existing is null;

        var kind = existing?.Kind ?? frontMatter.FrontMatter?.Kind ?? InferKind(relativePath);
        var topic = existing?.Topic ?? frontMatter.FrontMatter?.Topic ?? InferTopic(relativePath);
        var status = existing is not null
            ? existing.Status
            : frontMatter.FrontMatter?.Status.HasValue == true
                ? new DocStatusValue(kind, frontMatter.FrontMatter.Status.Value)
                : DocStatusValue.DefaultFor(kind);
        var order = existing?.Order ?? frontMatter.FrontMatter?.Order ?? InferOrder(relativePath);
        var supersededBy = existing?.SupersededBy ?? frontMatter.FrontMatter?.SupersededBy;
        var archived = existing?.Archived ?? false;

        var counts = kind == DocKind.Plan ? PlanCheckboxCounter.Count(body) : new CheckboxCounts(0, 0);
        var id = existing?.Id ?? DocId.NewId();
        var doc = new Doc(
            id, workspaceId, relativePath, kind,
            ExtractTitle(body) ?? Path.GetFileNameWithoutExtension(relativePath),
            status, topic, order, archived, supersededBy,
            counts.Total, counts.Done, hash,
            new DateTimeOffset(new FileInfo(absolutePath).LastWriteTimeUtc),
            frontMatterError: frontMatter.ErrorMessage is not null,
            frontMatterErrorMessage: frontMatter.ErrorMessage);

        var oldSections = existing is null ? [] : await _docsIndex.GetSectionsAsync(id.Value);
        var chunk = HeadingChunker.Chunk(body);
        var sections = new List<DocSection>();
        if (!string.IsNullOrWhiteSpace(chunk.Preamble))
            sections.Add(NewSection(id, "_preamble", "Preamble", 0, 1, chunk.Preamble.Split('\n').Length, kind, oldSections));
        foreach (var c in chunk.Sections)
            sections.Add(NewSection(id, c.Anchor, c.RawHeadingText, c.Level, c.LineStart, c.LineEnd, kind, oldSections));

        var refs = ExtractRefs(id, body, sections);
        var relations = new List<DocRelation>();
        if (isNew && !string.IsNullOrWhiteSpace(frontMatter.FrontMatter?.Spec))
            relations.Add(new DocRelation(id, await ResolveTargetAsync(frontMatter.FrontMatter!.Spec!, workspaceId, ct), RelationKind.Implements));
        foreach (Match match in Regex.Matches(body, @"\*\*Spec:\*\*\s*`?([^`\s]+)`?"))
            relations.Add(new DocRelation(id, await ResolveTargetAsync(match.Groups[1].Value, workspaceId, ct), RelationKind.Implements));
        foreach (Match match in Regex.Matches(body, @"\[[^\]]+\]\(([^)#]+\.md)[^)]*\)"))
            relations.Add(new DocRelation(id, await ResolveTargetAsync(match.Groups[1].Value, workspaceId, ct), RelationKind.References));

        return new DocParseResult(doc, sections, refs, relations.Distinct().ToList(), body);
    }

    private static DocSection NewSection(
        DocId id, string anchor, string heading, int level, int start, int end,
        DocKind kind, IReadOnlyList<DocSection> old)
    {
        var previous = old.FirstOrDefault(s => s.Anchor == anchor);
        return new DocSection(id, anchor, heading, level, start, end,
            previous?.Status ?? DocStatusValue.DefaultFor(kind),
            previous?.Annotation, previous?.StepsTotal ?? 0, previous?.StepsDone ?? 0);
    }

    private async Task<DocId> ResolveTargetAsync(string value, string workspaceId, CancellationToken ct)
    {
        var raw = value.Trim();
        if (DocId.TryParse(raw, out var parsed)) return parsed;
        var normalized = raw.Replace('\\', '/');
        var target = await _docsIndex.GetByPathAsync(workspaceId, normalized, ct);
        if (target is not null) return target.Id;
        foreach (var row in await _docsIndex.ListAsync(workspaceId, includeArchived: true))
        {
            if (row.Path.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase))
                return DocId.Parse(row.DocId);
        }
        return DocId.Parse(raw);
    }

    private static IReadOnlyList<DocRef> ExtractRefs(DocId docId, string body, IReadOnlyList<DocSection> sections)
    {
        var refs = new List<DocRef>();
        foreach (Match match in Regex.Matches(body, "`([^`]+)`"))
        {
            var raw = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = body[..match.Index].Count(c => c == '\n') + 1;
            var anchor = sections.LastOrDefault(s => s.LineStart <= line)?.Anchor ?? "_preamble";
            refs.Add(new DocRef(docId, anchor, raw));
        }
        return refs;
    }

    private static DocKind InferKind(string path)
    {
        if (path.Contains("/specs/", StringComparison.OrdinalIgnoreCase) || path.Contains("design", StringComparison.OrdinalIgnoreCase)) return DocKind.Spec;
        if (path.Contains("/plans/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("-plan.md", StringComparison.OrdinalIgnoreCase)) return DocKind.Plan;
        return DocKind.Doc;
    }

    private static string InferTopic(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 1 ? parts[^2] : "docs";
    }

    private static int InferOrder(string path)
        => int.TryParse(Path.GetFileNameWithoutExtension(path).Split('-').FirstOrDefault(), out var n) ? n : 0;

    private static string? ExtractTitle(string body)
        => Regex.Match(body, "^#\\s+(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim() is { Length: > 0 } title ? title : null;

    public async Task<Doc> CreateDocumentAsync(
        string workspaceRoot, string path, string content, DocKind kind,
        string? topic = null, int status = 0, int order = 0, CancellationToken ct = default)
    {
        var workspaceId = ResolveWorkspaceId(workspaceRoot);
        var relative = path.Replace('\\', '/');
        var absolute = Path.Combine(workspaceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, content, ct);
        var parse = await ParseAsync(absolute, relative, workspaceId, ComputeHash(content), null, ct);
        var doc = parse.Doc!;
        doc.Kind = kind;
        doc.Topic = topic ?? InferTopic(relative);
        doc.Status = new DocStatusValue(kind, status);
        doc.Order = order;
        await _docsIndex.IndexAsync(doc, parse.Sections, parse.Refs, parse.Body);
        foreach (var relation in parse.Relations) await _docsIndex.UpsertRelationAsync(relation);
        await WriteAuditAsync(doc.Id.Value, "create", null, null, "docs_service", ct);
        return doc;
    }

    public async Task<Doc?> UpdateDocumentAsync(DocId id, string content, CancellationToken ct = default)
    {
        var existing = await _docsIndex.GetAsync(id.Value);
        if (existing is null) return null;
        var root = ResolveRoot(existing);
        var absolute = Path.Combine(root, existing.Path);
        await File.WriteAllTextAsync(absolute, content, ct);
        var parse = await ParseAsync(absolute, existing.Path, existing.WorkspaceId, ComputeHash(content), existing, ct);
        await _docsIndex.IndexAsync(parse.Doc!, parse.Sections, parse.Refs, parse.Body);
        await WriteAuditAsync(id.Value, "update", null, null, "docs_service", ct);
        return parse.Doc;
    }

    public async Task<bool> ArchiveDocumentAsync(DocId id, string actor = "docs_service", CancellationToken ct = default)
    {
        var existing = await _docsIndex.GetAsync(id.Value);
        if (existing is null) return false;
        existing.Archived = true;
        var sections = await _docsIndex.GetSectionsAsync(id.Value);
        var refs = await _docsIndex.GetRefsAsync(id.Value);
        await _docsIndex.IndexAsync(existing, sections, refs);
        await WriteAuditAsync(id.Value, "delete", null, null, actor, ct);
        return true;
    }

    public async Task<bool> AnnotateSectionAsync(
        DocId id, string anchor, int status, string? annotation, string actor = "docs_service", CancellationToken ct = default)
    {
        var existing = await _docsIndex.GetAsync(id.Value);
        if (existing is null) return false;
        var sections = await _docsIndex.GetSectionsAsync(id.Value);
        var target = sections.FirstOrDefault(s => s.Anchor == anchor);
        if (target is null) return false;
        var updated = sections.Select(s => s.Anchor == anchor
            ? new DocSection(s.DocId, s.Anchor, s.Heading, s.Level, s.LineStart, s.LineEnd,
                new DocStatusValue(existing.Kind, status), annotation, s.StepsTotal, s.StepsDone)
            : s).ToList();
        await _docsIndex.IndexAsync(existing, updated, await _docsIndex.GetRefsAsync(id.Value));
        await WriteAuditAsync(id.Value, "annotate", null, annotation, actor, ct);
        return true;
    }

    public async Task AddRelationAsync(
        DocId sourceId, DocId targetId, RelationKind kind, string actor = "docs_service", CancellationToken ct = default)
    {
        var source = await _docsIndex.GetAsync(sourceId.Value) ?? throw new InvalidOperationException("Source document not found");
        var target = await _docsIndex.GetAsync(targetId.Value) ?? throw new InvalidOperationException("Target document not found");
        ValidateRelation(source.Kind, target.Kind, kind);
        await _docsIndex.UpsertRelationAsync(new DocRelation(sourceId, targetId, kind));
        await WriteAuditAsync(sourceId.Value, "relate", null, $"relation:{kind}:{targetId.Value}", actor, ct);
    }

    private static void ValidateRelation(DocKind sourceKind, DocKind targetKind, RelationKind kind)
    {
        if (kind == RelationKind.Implements && sourceKind != DocKind.Plan)
            throw new InvalidOperationException("Implements relation requires source to be Plan");
        if (kind == RelationKind.Supersedes && sourceKind != targetKind)
            throw new InvalidOperationException("Supersedes relation requires both documents to be same kind");
    }

    private string ResolveRoot(Doc doc)
        => _workspaceRoots.FirstOrDefault(r => ResolveWorkspaceId(r) == doc.WorkspaceId)
           ?? _workspaceRoots.FirstOrDefault() ?? Directory.GetCurrentDirectory();

    public Task<IReadOnlyList<DocListItem>> ListAsync(
        string? workspace = null, DocKind? kind = null, string? topic = null,
        string sort = "name", string order = "asc", CancellationToken ct = default)
        => _docsIndex.ListAsync(NormalizeWorkspace(workspace), kind, topic, sort, order);

    public async Task<DocPageResult> ListPageAsync(
        IReadOnlyList<string>? workspaces = null, DocKind? kind = null, string? topic = null,
        string sort = "name", string order = "asc", int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var ids = workspaces?.Select(NormalizeWorkspace).Where(id => id is not null).Distinct().ToList() ?? [];
        IReadOnlyList<DocListItem> items;
        if (ids.Count == 0)
            items = await _docsIndex.ListAsync(null, kind, topic, sort, order);
        else
        {
            var merged = new List<DocListItem>();
            foreach (var id in ids) merged.AddRange(await _docsIndex.ListAsync(id, kind, topic, sort, order));
            items = merged;
        }
        var key = sort?.ToLowerInvariant() switch
        {
            "path" => (Func<DocListItem, object>)(item => item.Path),
            "kind" => item => item.Kind,
            "status" => item => item.Status,
            "order" => item => item.Order,
            "updated" => item => item.MTime,
            _ => item => item.Title
        };
        var descending = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);
        items = (descending ? items.OrderByDescending(key) : items.OrderBy(key))
            .ThenBy(item => item.Title, StringComparer.Ordinal).ToList();
        var total = items.Count;
        var page = items.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500)).ToList().AsReadOnly();
        return new DocPageResult(page, total);
    }

    public Task<IReadOnlyList<DocSearchResult>> SearchAsync(
        string query, string? workspace = null, DocKind? kind = null, string? topic = null,
        int limit = 50, CancellationToken ct = default)
        => _docsIndex.SearchAsync(query, NormalizeWorkspace(workspace), kind, topic, null, limit);

    public async Task<DocReadResult?> ReadAsync(
        DocId id, bool includeSections = false, bool includeRefs = false, bool includeContent = false, CancellationToken ct = default)
    {
        var doc = await _docsIndex.GetAsync(id.Value);
        if (doc is null) return null;
        var sections = includeSections ? await _docsIndex.GetSectionsAsync(id.Value) : null;
        var refs = includeRefs ? await _docsIndex.GetRefsAsync(id.Value) : null;
        string? content = null;
        if (includeContent)
        {
            var absolute = Path.Combine(ResolveRoot(doc), doc.Path);
            if (File.Exists(absolute)) content = await File.ReadAllTextAsync(absolute, ct);
        }
        return new DocReadResult(doc, sections, refs, content);
    }

    public Task<IReadOnlyList<DocSection>> GetSectionsAsync(DocId id, CancellationToken ct = default)
        => _docsIndex.GetSectionsAsync(id.Value);
    public Task<IReadOnlyList<DocRef>> GetRefsAsync(DocId id, CancellationToken ct = default)
        => _docsIndex.GetRefsAsync(id.Value);
    public Task<IReadOnlyList<DocRelation>> GetRelationsAsync(DocId id, bool asSource, CancellationToken ct = default)
        => _docsIndex.GetRelationsAsync(id.Value, asSource);

    public async Task<StalenessReport> GetStalenessReportAsync(CancellationToken ct = default)
    {
        var findings = new List<StalenessFinding>();
        foreach (var item in await _docsIndex.ListAsync())
        {
            var doc = await _docsIndex.GetAsync(item.DocId);
            if (doc is null || string.IsNullOrWhiteSpace(doc.SupersededBy)) continue;
            if (await _docsIndex.GetAsync(doc.SupersededBy) is null)
                findings.Add(new StalenessFinding(item.DocId, item.Title, item.Path,
                    StalenessKind.SupersededWithoutReplacement, "Superseded by a document that does not exist", null));
        }
        return new StalenessReport(findings, DateTimeOffset.UtcNow);
    }

    public Task<CoveragePolicy> GetCoveragePolicyAsync(string? workspaceRoot = null, CancellationToken ct = default)
        => _docsIndex.GetPolicyAsync(ct);

    public Task SetCoveragePolicyAsync(CoveragePolicy coverage, string? workspaceRoot = null, CancellationToken ct = default)
        => _docsIndex.SetPolicyAsync(coverage, ct);

    public Task<IReadOnlyList<DocAuditEntry>> GetAuditAsync(string? docId = null, int limit = 100, CancellationToken ct = default)
        => _docsAudit.GetAsync(docId, limit, ct);

    public async Task WriteAuditAsync(
        string docId, string action, string? beforeJson, string? afterJson, string actor, CancellationToken ct = default)
    {
        await _docsAudit.WriteAsync(new DocAuditEntry(
            Ulid.NewUlid().ToString(), docId, actor, action, beforeJson, afterJson, DateTimeOffset.UtcNow), ct);
    }
}
