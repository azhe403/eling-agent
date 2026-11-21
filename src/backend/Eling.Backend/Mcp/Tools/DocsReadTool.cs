using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core.Docs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// MCP tools for the read side of the docs catalog: search, read, list, staleness, audit, relations.
/// </summary>
[McpServerToolType]
public sealed class DocsReadTool
{
    private readonly IDocsIndex _index;
    private readonly DocsService? _service;
    private readonly ILogger<DocsReadTool>? _logger;

    public DocsReadTool(IDocsIndex index, ILogger<DocsReadTool>? logger = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger;
    }

    public DocsReadTool(DocsService service, ILogger<DocsReadTool>? logger = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger;
    }

    public DocsReadTool(IDocsIndex index, DocsService service, ILogger<DocsReadTool>? logger = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger;
    }

    [McpServerTool(Name = "doc_search"), Description("Search documents using FTS5. Supports filters: ownership (managed/external), origin (current/ancestor/shard/global), kind (spec/plan/doc/scratch), topic, pathPrefix. Returns section-level results with rank.")]
    public async Task<IReadOnlyList<DocSearchResultDto>> SearchAsync(
        [Description("Search query — keywords to match against title, topic, heading, content.")] string query,
        [Description("Filter by ownership: managed or external.")] string? ownership = null,
        [Description("Filter by origin: current, ancestor, shard, global.")] string? origin = null,
        [Description("Filter by kind: spec, plan, doc, scratch.")] string? kind = null,
        [Description("Filter by topic (folder name).")] string? topic = null,
        [Description("Filter by path prefix (e.g. 'docs/specs/').")] string? pathPrefix = null,
        [Description("Maximum number of results. Default 10.")] int limit = 10)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("Query cannot be empty.", nameof(query));
            }

            var trimmedQuery = query.Trim();
            var trimmedOwnership = ParseOwnership(ownership);
            var trimmedOrigin = ParseOrigin(origin);
            var trimmedKind = ParseKind(kind);

            var results = await _index.SearchAsync(trimmedQuery, trimmedOwnership, trimmedOrigin, trimmedKind, topic?.Trim(), pathPrefix?.Trim(), limit);
            var dtos = results.Select(DocSearchResultDto.From).ToList().AsReadOnly();

            _logger?.LogInformation("doc_search '{Query}' returned {Count} results", trimmedQuery, dtos.Count);
            return dtos;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_search threw (query={Query})", query);
            throw;
        }
    }

    [McpServerTool(Name = "doc_read"), Description("Read a document by ID. Supports modes: 'full' (entire doc with all sections), 'section' (single section by anchor), 'map' (heading tree only).")]
    public async Task<DocReadResultDto?> ReadAsync(
        [Description("Document ID (format: ownership:origin:sha1).")] string id,
        [Description("Read mode: full, section, map. Default full.")] string mode = "full",
        [Description("Section anchor (required when mode=section).")] string? anchor = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("ID cannot be empty.", nameof(id));
            }

            var docId = DocId.Parse(id);
            var doc = await _index.GetAsync(docId.Value);
            if (doc == null)
            {
                return null;
            }

            var sections = await _index.GetSectionsAsync(docId.Value);
            var refs = await _index.GetRefsAsync(docId.Value);

            var trimmedMode = mode.Trim().ToLowerInvariant();

            if (trimmedMode == "section")
            {
                if (string.IsNullOrWhiteSpace(anchor))
                {
                    throw new ArgumentException("Anchor is required when mode=section.", nameof(anchor));
                }
                var section = sections.FirstOrDefault(s => s.Anchor == anchor);
                if (section == null)
                {
                    return null;
                }
                return new DocReadResultDto(DocDto.From(doc), [DocSectionDto.From(section)], []);
            }

            if (trimmedMode == "map")
            {
                var headingTree = sections
                    .Where(s => s.Level > 0)
                    .Select(s => new HeadingTreeDto(s.Anchor, s.Heading, s.Level))
                    .ToList();
                return new DocReadResultDto(DocDto.From(doc), [], [], headingTree);
            }

            // Full mode
            return new DocReadResultDto(
                DocDto.From(doc),
                sections.Select(DocSectionDto.From).ToList(),
                refs.Select(DocRefDto.From).ToList());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_read threw (id={Id})", id);
            throw;
        }
    }

    [McpServerTool(Name = "doc_list"), Description("List documents grouped by topic, ordered by read_order. Filters: ownership, origin, kind, topic.")]
    public async Task<DocListDto> ListAsync(
        [Description("Filter by ownership: managed, external.")] string? ownership = null,
        [Description("Filter by origin: current, ancestor, shard, global.")] string? origin = null,
        [Description("Filter by kind: spec, plan, doc, scratch.")] string? kind = null,
        [Description("Filter by topic (folder name).")] string? topic = null,
        [Description("Sort: topic (default), order, title, mtime.")] string sort = "topic")
    {
        try
        {
            var trimmedOwnership = ParseOwnership(ownership);
            var trimmedOrigin = ParseOrigin(origin);
            var trimmedKind = ParseKind(kind);

            var items = await _index.ListAsync(trimmedOwnership, trimmedOrigin, trimmedKind, topic?.Trim(), sort);
            var dtos = items.Select(DocListItemDto.From).ToList().AsReadOnly();

            // Group by topic
            var grouped = dtos
                .GroupBy(d => d.Topic)
                .Select(g => new DocTopicGroupDto(g.Key, g.ToList()))
                .OrderBy(g => g.Topic)
                .ToList();

            _logger?.LogInformation("doc_list returned {Count} docs in {Topics} topics", dtos.Count, grouped.Count);
            return new DocListDto(grouped);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_list threw");
            throw;
        }
    }

    [McpServerTool(Name = "doc_staleness"), Description("Generate staleness report: unresolved references, superseded without replacement, draft specs > 30 days, untouched > 180 days. Covers all documents including external.")]
    public async Task<StalenessReportDto> GetStalenessReportAsync()
    {
        try
        {
            var report = await _service!.GetStalenessReportAsync();
            var findings = report.Findings.Select(f => new StalenessFindingDto(
                f.DocId, f.Title, f.RelPath, f.Kind.ToDto(), f.Message, f.Anchor)).ToList();

            _logger?.LogInformation("doc_staleness returned {Count} findings", findings.Count);
            return new StalenessReportDto(findings, report.GeneratedAt);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_staleness threw");
            throw;
        }
    }

    [McpServerTool(Name = "doc_audit"), Description("Get audit entries for a document or scope. Returns empty for external documents.")]
    public async Task<IReadOnlyList<DocAuditEntryDto>> GetAuditAsync(
        [Description("Optional document ID to filter. Omit for all managed docs.")] string? docId = null,
        [Description("Maximum entries. Default 100.")] int limit = 100)
    {
        try
        {
            var entries = await _index.GetAuditAsync(docId?.Trim(), limit);
            var dtos = entries.Select(DocAuditEntryDto.From).ToList().AsReadOnly();

            _logger?.LogInformation("doc_audit returned {Count} entries (docId={DocId})", dtos.Count, docId ?? "all");
            return dtos;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_audit threw (docId={DocId})", docId);
            throw;
        }
    }

    [McpServerTool(Name = "doc_relation"), Description("Read spec↔plan relations. Returns both directions: Implements (plan→spec), Supersedes, References.")]
    public async Task<IReadOnlyList<DocRelationDto>> GetRelationsAsync(
        [Description("Document ID to get relations for.")] string docId,
        [Description("Direction: both (default), incoming, outgoing.")] string direction = "both")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(docId))
            {
                throw new ArgumentException("docId cannot be empty.", nameof(docId));
            }

            var id = DocId.Parse(docId);
            var asSource = direction != "incoming";
            var relations = await _index.GetRelationsAsync(id.Value, asSource);

            // If direction is both, also get incoming
            if (direction == "both")
            {
                var incoming = await _index.GetRelationsAsync(id.Value, false);
                relations = relations.Concat(incoming).DistinctBy(r => (r.SourceId, r.TargetId, r.Kind)).ToList();
            }

            var dtos = relations.Select(DocRelationDto.From).ToList().AsReadOnly();
            _logger?.LogInformation("doc_relation returned {Count} relations (docId={DocId}, direction={Direction})", dtos.Count, docId, direction);
            return dtos;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_relation threw (docId={DocId})", docId);
            throw;
        }
    }

    private static DocOwnership? ParseOwnership(string? ownership)
    {
        if (string.IsNullOrWhiteSpace(ownership)) return null;
        return ownership.Trim().ToLowerInvariant() switch
        {
            "managed" => DocOwnership.Managed,
            "external" => DocOwnership.External,
            _ => throw new ArgumentException($"Invalid ownership '{ownership}'. Use 'managed' or 'external'.")
        };
    }

    private static DocOrigin? ParseOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        return origin.Trim().ToLowerInvariant() switch
        {
            "current" => DocOrigin.Current,
            "ancestor" => DocOrigin.Ancestor,
            "shard" => DocOrigin.Shard,
            "global" => DocOrigin.Global,
            _ => throw new ArgumentException($"Invalid origin '{origin}'. Use 'current', 'ancestor', 'shard', or 'global'.")
        };
    }

    private static DocKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        return kind.Trim().ToLowerInvariant() switch
        {
            "spec" => DocKind.Spec,
            "plan" => DocKind.Plan,
            "doc" => DocKind.Doc,
            "scratch" => DocKind.Scratch,
            _ => throw new ArgumentException($"Invalid kind '{kind}'. Use 'spec', 'plan', 'doc', or 'scratch'.")
        };
    }
}