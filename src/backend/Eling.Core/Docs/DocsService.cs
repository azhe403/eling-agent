namespace Eling.Core.Docs;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Coordinates Markdown content, committed per-workspace JSON metadata, the
/// disposable global index, and the persistent audit store. Ownership and
/// origin were removed: every Markdown file is a doc.
/// </summary>
public sealed class DocsService
{
    private readonly IDocsIndex _index;
    private readonly IDocsAuditStore _audit;
    private readonly DocsMetadataStore _metadata;
    private readonly ILogger<DocsService> _logger;
    private readonly List<string> _workspaceRoots;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public DocsService(
        IDocsIndex index,
        IDocsAuditStore audit,
        DocsMetadataStore metadata,
        IEnumerable<string> workspaceRoots,
        ILogger<DocsService>? logger = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _workspaceRoots = workspaceRoots?.ToList() ?? [];
        _logger = logger ?? NullLogger<DocsService>.Instance;
    }

    public IReadOnlyList<string> WorkspaceRoots => _workspaceRoots.AsReadOnly();

    public void SetWorkspaceRoots(IEnumerable<string> roots)
    {
        _workspaceRoots.Clear();
        _workspaceRoots.AddRange(roots);
    }

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
                var workspaceId = DocsMetadataStore.ResolveWorkspaceId(root);
                var (document, findings) = await _metadata.LoadWithFindingsAsync(root, ct);
                messages.AddRange(findings);
                var knownPaths = document.Docs.Keys.ToHashSet(StringComparer.Ordinal);

                var files = paths?.Where(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList()
                            ?? EnumerateMarkdown(root, document.Coverage).ToList();
                foreach (var file in files)
                {
                    var path = GetRelativePath(file, root);
                    var hash = ComputeHash(await File.ReadAllTextAsync(file, ct));
                    var entry = _metadata.GetOrCreate(document, path, workspaceId);
                    var existing = await _index.GetAsync(entry.Id);
                    if (!full && existing?.FileHash == hash) { skipped++; knownPaths.Remove(path); continue; }

                    try
                    {
                        var parse = await ParseDocumentAsync(file, path, hash, entry, ct);
                        await _index.IndexAsync(parse.Doc!, parse.Sections, parse.Refs, parse.Body);
                        foreach (var relation in parse.Relations) await _index.UpsertRelationAsync(relation);
                        indexed++;
                        knownPaths.Remove(path);
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        messages.Add($"Error indexing {file}: {ex.Message}");
                        _logger.LogWarning(ex, "Error indexing file {File}", file);
                        entry.FileHash = null;
                    }
                }

                if (!full)
                {
                    foreach (var missing in knownPaths)
                    {
                        if (!document.Docs.TryGetValue(missing, out var entry) || entry is null) continue;
                        await _index.DeleteAsync(entry.Id);
                        deleted++;
                    }
                }
                await _metadata.SaveAsync(root, document, ct);
            }
            return new ScanResult(indexed, skipped, deleted, errors, roots.Length, false, messages);
        }
        finally { _scanGate.Release(); }
    }

    private static IEnumerable<string> EnumerateMarkdown(string root, CoveragePolicy coverage)
    {
        if (!Directory.Exists(root)) yield break;
        var excluded = new[] { ".git", "node_modules", "bin", "obj", ".next", ".superpowers", ".eling" };
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = GetRelativePath(file, root);
            if (excluded.Any(part => relative.Split('/').Contains(part, StringComparer.OrdinalIgnoreCase))) continue;
            if (coverage.Exclude?.Any(pattern => MatchPattern(relative, pattern)) == true) continue;
            yield return file;
        }
    }

    private static bool MatchPattern(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var trimmed = pattern.TrimEnd('/').Replace("**/", "");
        return path.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/" + trimmed, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(trimmed + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRelativePath(string file, string root)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    private async Task<DocParseResult> ParseDocumentAsync(
        string absolutePath, string relativePath, string hash, DocsMetadataEntry entry, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(absolutePath, ct);
        var frontMatter = DocFrontMatterParser.Parse(content, _logger);
        var body = frontMatter.RemainingContent;
        var workspaceId = entry.WorkspaceId;

        // Legacy front-matter is an import source only: it seeds empty metadata.
        if (entry.Kind == default && frontMatter.FrontMatter?.Kind.HasValue == true)
            entry.Kind = frontMatter.FrontMatter.Kind.Value;
        if (string.IsNullOrWhiteSpace(entry.Topic) && !string.IsNullOrWhiteSpace(frontMatter.FrontMatter?.Topic))
            entry.Topic = frontMatter.FrontMatter.Topic!;
        if (entry.Status == 0 && frontMatter.FrontMatter?.Status.HasValue == true)
            entry.Status = frontMatter.FrontMatter.Status.Value;
        if (string.IsNullOrWhiteSpace(entry.SupersededBy) && !string.IsNullOrWhiteSpace(frontMatter.FrontMatter?.SupersededBy))
            entry.SupersededBy = frontMatter.FrontMatter.SupersededBy;
        if (entry.Relations.Count == 0 && !string.IsNullOrWhiteSpace(frontMatter.FrontMatter?.Spec))
            entry.Relations.Add(new DocRelationMeta { TargetPath = frontMatter.FrontMatter!.Spec!, Kind = RelationKind.Implements });

        var kind = entry.Kind != default ? entry.Kind : InferKind(relativePath);
        var topic = string.IsNullOrWhiteSpace(entry.Topic) ? InferTopic(relativePath) : entry.Topic;
        var status = entry.Status != 0
            ? new DocStatusValue(kind, entry.Status)
            : DocStatusValue.DefaultFor(kind);
        var order = entry.Order != 0 ? entry.Order : InferOrder(relativePath);

        var chunk = HeadingChunker.Chunk(body);
        var sections = new List<DocSection>();
        if (!string.IsNullOrWhiteSpace(chunk.Preamble))
            sections.Add(new DocSection(DocId.Empty, "_preamble", "Preamble", 0, 1,
                chunk.Preamble.Split('\n').Length, DocStatusValue.DefaultFor(kind)));
        foreach (var c in chunk.Sections)
            sections.Add(new DocSection(DocId.Empty, c.Anchor, c.RawHeadingText, c.Level, c.LineStart, c.LineEnd,
                DocStatusValue.DefaultFor(kind)));

        var counts = kind == DocKind.Plan ? PlanCheckboxCounter.Count(body) : new CheckboxCounts(0, 0);
        var docId = DocId.Parse(entry.Id);
        var doc = new Doc(
            docId, workspaceId, relativePath, kind,
            ExtractTitle(body) ?? Path.GetFileNameWithoutExtension(relativePath),
            status, topic, order, entry.Archived, entry.SupersededBy,
            counts.Total, counts.Done, hash, new DateTimeOffset(new FileInfo(absolutePath).LastWriteTimeUtc));

        sections = sections.Select(s => new DocSection(
            docId, s.Anchor, s.Heading, s.Level, s.LineStart, s.LineEnd, s.Status,
            entry.Annotations.TryGetValue(s.Anchor, out var note) ? note.Text : null,
            s.StepsTotal, s.StepsDone)).ToList();

        var refs = ExtractRefs(docId, body, sections);
        var relations = entry.Relations
            .Where(r => !string.IsNullOrWhiteSpace(r.TargetPath))
            .Select(r => new DocRelation(docId, DocId.Parse(r.TargetPath), r.Kind))
            .Concat(ExtractRelations(docId, body, workspaceId))
            .Distinct()
            .ToList();

        return new DocParseResult(doc, sections, refs, relations, body);
    }

    private static IReadOnlyList<DocRef> ExtractRefs(DocId docId, string body, IReadOnlyList<DocSection> sections)
    {
        var refs = new List<DocRef>();
        foreach (Match match in Regex.Matches(body, "`([^`]+)`"))
        {
            var raw = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var anchor = sections.LastOrDefault(s =>
                s.LineStart <= body[..match.Index].Count(c => c == '\n') + 1)?.Anchor ?? "_preamble";
            refs.Add(new DocRef(docId, anchor, raw));
        }
        return refs;
    }

    private static IReadOnlyList<DocRelation> ExtractRelations(DocId docId, string body, string workspaceId)
    {
        var relations = new List<DocRelation>();
        foreach (Match match in Regex.Matches(body, @"\*\*Spec:\*\*\s*`?([^`\s]+)`?"))
            relations.Add(new DocRelation(docId, DocId.Parse(match.Groups[1].Value.Trim()), RelationKind.Implements));
        foreach (Match match in Regex.Matches(body, @"\[[^\]]+\]\(([^)#]+\.md)[^)]*\)"))
            relations.Add(new DocRelation(docId, DocId.Parse(match.Groups[1].Value.Trim()), RelationKind.References));
        return relations.Distinct().ToList();
    }

    private static DocKind InferKind(string path)
    {
        if (path.Contains("/specs/", StringComparison.OrdinalIgnoreCase) || path.Contains("design", StringComparison.OrdinalIgnoreCase)) return DocKind.Spec;
        if (path.Contains("/plans/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("-plan.md", StringComparison.OrdinalIgnoreCase)) return DocKind.Plan;
        if (path.Contains("/rca/", StringComparison.OrdinalIgnoreCase)) return DocKind.Doc;
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

    private string ResolveRoot(Doc doc)
        => _workspaceRoots.FirstOrDefault(r => DocsMetadataStore.ResolveWorkspaceId(r) == doc.WorkspaceId)
           ?? _workspaceRoots.FirstOrDefault() ?? Directory.GetCurrentDirectory();

    public async Task<Doc> CreateDocumentAsync(
        string workspaceRoot, string path, string content, DocKind kind,
        string? topic = null, int status = 0, int order = 0, CancellationToken ct = default)
    {
        var workspaceId = DocsMetadataStore.ResolveWorkspaceId(workspaceRoot);
        var (document, _) = await _metadata.LoadWithFindingsAsync(workspaceRoot, ct);
        var entry = _metadata.GetOrCreate(document, path.Replace('\\', '/'), workspaceId);
        entry.Kind = kind;
        entry.Topic = topic ?? InferTopic(path);
        entry.Status = status;
        entry.Order = order;
        var absolute = Path.Combine(workspaceRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, content, ct);
        var parse = await ParseDocumentAsync(absolute, entry.Path, ComputeHash(content), entry, ct);
        await _index.IndexAsync(parse.Doc!, parse.Sections, parse.Refs, parse.Body);
        await _metadata.SaveAsync(workspaceRoot, document, ct);
        await WriteAuditAsync(parse.Doc!.Id.Value, "create", null, null, "docs_service", ct);
        return parse.Doc;
    }

    public async Task<Doc?> UpdateDocumentAsync(DocId id, string content, CancellationToken ct = default)
    {
        var existing = await _index.GetAsync(id.Value);
        if (existing is null) return null;
        var root = ResolveRoot(existing);
        var (document, _) = await _metadata.LoadWithFindingsAsync(root, ct);
        if (!document.Docs.TryGetValue(existing.Path, out var entry) || entry is null) return null;
        var absolute = Path.Combine(root, existing.Path);
        await File.WriteAllTextAsync(absolute, content, ct);
        var parse = await ParseDocumentAsync(absolute, existing.Path, ComputeHash(content), entry, ct);
        await _index.IndexAsync(parse.Doc!, parse.Sections, parse.Refs, parse.Body);
        await _metadata.SaveAsync(root, document, ct);
        await WriteAuditAsync(id.Value, "update", null, null, "docs_service", ct);
        return parse.Doc;
    }

    public async Task<bool> ArchiveDocumentAsync(DocId id, string actor = "docs_service", CancellationToken ct = default)
    {
        var existing = await _index.GetAsync(id.Value);
        if (existing is null) return false;
        var root = ResolveRoot(existing);
        var (document, _) = await _metadata.LoadWithFindingsAsync(root, ct);
        if (!document.Docs.TryGetValue(existing.Path, out var entry) || entry is null) return false;
        entry.Archived = true;
        await _metadata.SaveAsync(root, document, ct);
        await _index.DeleteAsync(id.Value);
        await WriteAuditAsync(id.Value, "delete", null, null, actor, ct);
        return true;
    }

    public async Task<bool> AnnotateSectionAsync(
        DocId id, string anchor, int status, string? annotation, string actor = "docs_service", CancellationToken ct = default)
    {
        var existing = await _index.GetAsync(id.Value);
        if (existing is null) return false;
        var root = ResolveRoot(existing);
        var (document, _) = await _metadata.LoadWithFindingsAsync(root, ct);
        if (!document.Docs.TryGetValue(existing.Path, out var entry) || entry is null) return false;
        entry.Annotations[anchor] = new DocSectionAnnotation { Status = status, Text = annotation };
        await _metadata.SaveAsync(root, document, ct);
        var sections = await _index.GetSectionsAsync(id.Value);
        var refs = await _index.GetRefsAsync(id.Value);
        var updated = sections.Select(s => s.Anchor == anchor
            ? new DocSection(s.DocId, s.Anchor, s.Heading, s.Level, s.LineStart, s.LineEnd,
                new DocStatusValue(existing.Kind, status), annotation, s.StepsTotal, s.StepsDone)
            : s).ToList();
        await _index.IndexAsync(existing, updated, refs);
        await WriteAuditAsync(id.Value, "annotate", null, annotation, actor, ct);
        return true;
    }

    private static void ValidateRelation(DocKind sourceKind, DocKind targetKind, RelationKind kind)
    {
        if (kind == RelationKind.Implements && sourceKind != DocKind.Plan)
            throw new InvalidOperationException("Implements relation requires source to be Plan");
        if (kind == RelationKind.Supersedes && sourceKind != targetKind)
            throw new InvalidOperationException("Supersedes relation requires both documents to be same kind");
    }

    public async Task AddRelationAsync(
        DocId sourceId, DocId targetId, RelationKind kind, string actor = "docs_service", CancellationToken ct = default)
    {
        var source = await _index.GetAsync(sourceId.Value) ?? throw new InvalidOperationException("Source document not found");
        var target = await _index.GetAsync(targetId.Value) ?? throw new InvalidOperationException("Target document not found");
        ValidateRelation(source.Kind, target.Kind, kind);
        await _index.UpsertRelationAsync(new DocRelation(sourceId, targetId, kind));
        var root = ResolveRoot(source);
        var (document, _) = await _metadata.LoadWithFindingsAsync(root, ct);
        if (document.Docs.TryGetValue(source.Path, out var entry) && entry is not null)
        {
            entry.Relations.Add(new DocRelationMeta { TargetPath = target.Path, Kind = kind });
            await _metadata.SaveAsync(root, document, ct);
        }
        await WriteAuditAsync(sourceId.Value, "relate", null, $"relation:{kind}:{targetId.Value}", actor, ct);
    }

    public Task<IReadOnlyList<DocListItem>> ListAsync(
        string? workspace = null, DocKind? kind = null, string? topic = null,
        string sort = "name", string order = "asc", CancellationToken ct = default)
        => _index.ListAsync(workspace, kind, topic, sort, order);

    public Task<IReadOnlyList<DocSearchResult>> SearchAsync(
        string query, string? workspace = null, DocKind? kind = null, string? topic = null,
        int limit = 50, CancellationToken ct = default)
        => _index.SearchAsync(query, workspace, kind, topic, null, limit);

    public async Task<DocReadResult?> ReadAsync(
        DocId id, bool includeSections = false, bool includeRefs = false, bool includeContent = false, CancellationToken ct = default)
    {
        var doc = await _index.GetAsync(id.Value);
        if (doc is null) return null;
        var sections = includeSections ? await _index.GetSectionsAsync(id.Value) : null;
        var refs = includeRefs ? await _index.GetRefsAsync(id.Value) : null;
        string? content = null;
        if (includeContent)
        {
            var absolute = Path.Combine(ResolveRoot(doc), doc.Path);
            if (File.Exists(absolute)) content = await File.ReadAllTextAsync(absolute, ct);
        }
        return new DocReadResult(doc, sections, refs, content);
    }

    public Task<IReadOnlyList<DocAuditEntry>> GetAuditAsync(string? docId = null, int limit = 100, CancellationToken ct = default)
        => _audit.GetAsync(docId, limit, ct);

    public async Task WriteAuditAsync(
        string docId, string action, string? beforeJson, string? afterJson, string actor, CancellationToken ct = default)
    {
        await _audit.WriteAsync(new DocAuditEntry(
            Ulid.NewUlid().ToString(), docId, actor, action, beforeJson, afterJson, DateTimeOffset.UtcNow), ct);
    }
}
