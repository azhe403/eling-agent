using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core.Docs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>Read-side MCP tools for the unified docs catalog.</summary>
[McpServerToolType]
public sealed class DocsReadTool
{
    private readonly DocsService _docsService;
    private readonly ILogger<DocsReadTool>? _logger;

    public DocsReadTool(DocsService service, ILogger<DocsReadTool>? logger = null)
    {
        _docsService = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger;
    }

    [McpServerTool(Name = "doc_search"), Description("Search documents using FTS5. Filters: workspace, kind, topic, pathPrefix. Returns section-level results with rank.")]
    public async Task<IReadOnlyList<DocSearchResultDto>> SearchAsync(
        [Description("Search query — keywords to match against title, topic, heading, content.")] string query,
        [Description("Filter by workspace root; omit for all workspaces.")] string? workspace = null,
        [Description("Filter by kind: spec, plan, doc, scratch.")] string? kind = null,
        [Description("Filter by topic (folder name).")] string? topic = null,
        [Description("Filter by path prefix (e.g. 'docs/specs/').")] string? pathPrefix = null,
        [Description("Maximum number of results. Default 10.")] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query cannot be empty.", nameof(query));
        var results = await _docsService.SearchAsync(query.Trim(), workspace?.Trim(), ParseKind(kind), topic?.Trim(), limit);
        if (!string.IsNullOrWhiteSpace(pathPrefix))
            results = results.Where(r => r.Path.StartsWith(pathPrefix.Trim(), StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();
        return results.Select(DocSearchResultDto.From).ToList().AsReadOnly();
    }

    [McpServerTool(Name = "doc_list"), Description("List documents grouped by topic, ordered by server-side sort. Workspace filter: omit for all.")]
    public async Task<IReadOnlyList<DocListItemDto>> ListAsync(
        [Description("Filter by workspace root; omit for all workspaces.")] string? workspace = null,
        [Description("Filter by kind: spec, plan, doc, scratch.")] string? kind = null,
        [Description("Filter by topic.")] string? topic = null,
        [Description("Sort key: name (default), path, kind, status, order, updated.")] string? sort = null,
        [Description("Direction: asc (default) or desc.")] string? order = null)
    {
        var items = await _docsService.ListAsync(workspace?.Trim(), ParseKind(kind), topic?.Trim(), sort ?? "name", order ?? "asc");
        return items.Select(DocListItemDto.From).ToList().AsReadOnly();
    }

    [McpServerTool(Name = "doc_read"), Description("Read a document: full, section, or map (heading tree).")]
    public async Task<DocReadResultDto?> ReadAsync(
        [Description("Document id (ULID).")] string id,
        [Description("Mode: full, section, or map.")] string mode = "full",
        [Description("Section anchor when mode=section.")] string? anchor = null)
    {
        var result = await _docsService.ReadAsync(ParseId(id, nameof(id)), includeSections: true, includeRefs: true, includeContent: true);
        if (result is null) return null;
        var sectionDtos = (result.Sections ?? Array.Empty<DocSection>()).Select(DocSectionDto.From).ToList().AsReadOnly();
        var refDtos = (result.References ?? Array.Empty<DocRef>()).Select(DocRefDto.From).ToList().AsReadOnly();
        if (mode.Equals("map", StringComparison.OrdinalIgnoreCase))
            return new DocReadResultDto(DocDto.From(result.Document), sectionDtos, refDtos, null, null);
        if (mode.Equals("section", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(anchor))
        {
            var section = sectionDtos.Where(s => s.Anchor == anchor).ToList().AsReadOnly();
            return new DocReadResultDto(DocDto.From(result.Document), section, Array.Empty<DocRefDto>().ToList().AsReadOnly(), null, null);
        }
        return DocReadResultDto.From(result);
    }

    [McpServerTool(Name = "doc_staleness"), Description("Staleness findings across docs.")]
    public async Task<StalenessReport> StalenessAsync()
        => await _docsService.GetStalenessReportAsync();

    [McpServerTool(Name = "doc_audit"), Description("Audit entries for a document or scope.")]
    public async Task<IReadOnlyList<DocAuditEntryDto>> AuditAsync(
        [Description("Document id; omit for all.")] string? docId = null,
        [Description("Maximum entries. Default 100.")] int limit = 100)
        => (await _docsService.GetAuditAsync(docId?.Trim(), limit)).Select(DocAuditEntryDto.From).ToList().AsReadOnly();

    [McpServerTool(Name = "doc_relation"), Description("Read relations for a document, or write one when sourceId, targetId and kind are supplied.")]
    public async Task<IReadOnlyList<DocRelationDto>> RelationAsync(
        [Description("Document id for a read.")] string? docId = null,
        [Description("Source id for a write.")] string? sourceId = null,
        [Description("Target id for a write.")] string? targetId = null,
        [Description("Relation kind: implements, supersedes, references.")] string? kind = null,
        [Description("Actor for the audit row.")] string actor = "mcp:doc_relation")
    {
        if (sourceId is not null && targetId is not null && kind is not null)
        {
            if (!Enum.TryParse<RelationKind>(kind, true, out var parsed))
                throw new ArgumentException($"Invalid relation kind '{kind}'.", nameof(kind));
            await _docsService.AddRelationAsync(ParseId(sourceId, nameof(sourceId)), ParseId(targetId, nameof(targetId)), parsed, actor);
        }
        var id = docId ?? sourceId;
        if (string.IsNullOrWhiteSpace(id)) return Array.Empty<DocRelationDto>().ToList().AsReadOnly();
        var asSource = docId is null;
        var relations = await _docsService.GetRelationsAsync(ParseId(id, nameof(id)), asSource);
        return relations.Select(DocRelationDto.From).ToList().AsReadOnly();
    }

    private static DocId ParseId(string value, string paramName)
    {
        if (!DocId.TryParse(value, out var parsed))
            throw new ArgumentException(
                $"The document id '{value}' is not a valid ULID - check the id and try again.", paramName);
        return parsed;
    }

    [McpServerTool(Name = "doc_scope_policy"), Description("Inspect or change the workspace's docs coverage policy.")]
    public async Task<CoveragePolicy> ScopePolicyAsync(
        [Description("Workspace root; omit for the current workspace.")] string? workspace = null,
        [Description("Optional JSON include globs to set.")] string? include = null,
        [Description("Optional JSON exclude globs to set.")] string? exclude = null)
    {
        var current = await _docsService.GetCoveragePolicyAsync(workspace?.Trim());
        if (include is not null) current.Include = System.Text.Json.JsonSerializer.Deserialize<string[]>(include) ?? [];
        if (exclude is not null) current.Exclude = System.Text.Json.JsonSerializer.Deserialize<string[]>(exclude) ?? [];
        if (include is not null || exclude is not null)
            await _docsService.SetCoveragePolicyAsync(current, workspace?.Trim());
        return current;
    }

    private static DocKind? ParseKind(string? value)
        => string.IsNullOrWhiteSpace(value) ? null
            : Enum.TryParse<DocKind>(value, true, out var kind) ? kind : null;
}
