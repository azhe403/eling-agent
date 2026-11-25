using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core.Docs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>Write-side MCP tools for the unified docs catalog.</summary>
[McpServerToolType]
public sealed class DocsWriteTool
{
    private readonly DocsService _docsService;
    private readonly ILogger<DocsWriteTool>? _logger;

    public DocsWriteTool(DocsService service, ILogger<DocsWriteTool>? logger = null)
    {
        _docsService = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger;
    }

    [McpServerTool(Name = "doc_save"), Description("Create or update a doc: writes Markdown content and committed JSON metadata. No promote flag.")]
    public async Task<DocDto> SaveAsync(
        [Description("Workspace-relative Markdown path.")] string relPath,
        [Description("Markdown content only — no YAML front-matter.")] string content,
        [Description("Kind: spec, plan, doc, scratch.")] string kind = "doc",
        [Description("Topic (folder name).")] string? topic = null,
        [Description("Status value for the kind.")] int? status = null,
        [Description("Read order.")] int order = 0,
        [Description("Workspace root; omit for the current workspace.")] string? workspace = null)
    {
        if (string.IsNullOrWhiteSpace(relPath)) throw new ArgumentException("Path cannot be empty.", nameof(relPath));
        if (!Enum.TryParse<DocKind>(kind, true, out var parsedKind))
            throw new ArgumentException($"Invalid kind '{kind}'.", nameof(kind));
        var root = workspace?.Trim() ?? _docsService.WorkspaceRoots.FirstOrDefault()
                   ?? throw new InvalidOperationException("No workspace is registered.");
        var doc = await _docsService.CreateDocumentAsync(root, relPath, content, parsedKind, topic?.Trim(), status ?? 0, order);
        return DocDto.From(doc);
    }

    [McpServerTool(Name = "doc_annotate"), Description("Annotate a section of a doc. Stored in JSON metadata, never in the Markdown.")]
    public async Task<bool> AnnotateAsync(
        [Description("Document id (ULID).")] string docId,
        [Description("Section anchor.")] string anchor,
        [Description("Status value.")] int status,
        [Description("Annotation text.")] string? annotation = null,
        [Description("Actor for the audit row.")] string actor = "mcp:doc_annotate")
        => await _docsService.AnnotateSectionAsync(DocId.Parse(docId), anchor, status, annotation, actor);

    [McpServerTool(Name = "doc_index"), Description("Incremental rescan of one workspace or all registered workspaces.")]
    public async Task<ScanResult> IndexAsync(
        [Description("Full rebuild instead of incremental.")] bool full = false,
        [Description("Workspace root; omit for all.")] string? workspace = null,
        [Description("Optional exact paths to scan.")] string[]? paths = null)
        => await _docsService.ScanAsync(full, paths, workspace?.Trim());
}
