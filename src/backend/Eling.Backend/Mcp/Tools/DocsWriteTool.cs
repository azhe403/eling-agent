using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core.Docs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// MCP tools for the write side of the docs catalog: save, annotate, promote, relation, index, scope policy, offer.
/// </summary>
[McpServerToolType]
public sealed class DocsWriteTool
{
    private readonly DocsService _service;
    private readonly ILogger<DocsWriteTool>? _logger;

    public DocsWriteTool(DocsService service, ILogger<DocsWriteTool>? logger = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger;
    }

    [McpServerTool(Name = "doc_save"), Description("Create or update a managed document. Auto-generates front-matter. Use promote: true to write directly to docs/ as external. Refused for external documents (use promote instead).")]
    public async Task<DocSaveResponse> SaveAsync(
        [Description("Relative path within workspace (e.g. '.eling/docs/specs/my-spec.md' or 'docs/specs/my-spec.md').")] string relPath,
        [Description("Markdown content of the document.")] string content,
        [Description("Document kind: spec, plan, doc, scratch. Default inferred from path.")] string? kind = null,
        [Description("Topic (folder name). Default inferred from path.")] string? topic = null,
        [Description("Status value (per-kind enum). Default per kind.")] int? status = null,
        [Description("Read order. Default inferred from filename.")] int? order = null,
        [Description("If true, write to docs/ as external document instead of managed.")] bool promote = false,
        [Description("Actor for audit trail. Default 'mcp:doc_save'.")] string actor = "mcp:doc_save")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relPath))
            {
                throw new ArgumentException("relPath cannot be empty.", nameof(relPath));
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new ArgumentException("content cannot be empty.", nameof(content));
            }

            var trimmedRelPath = relPath.Trim().Replace('\\', '/');
            var docKind = ParseKindOrDefault(kind, trimmedRelPath);

            DocStatusValue? statusValue = null;
            if (status.HasValue)
            {
                statusValue = new DocStatusValue(docKind, status.Value);
            }

            if (promote)
            {
                // Create directly as external in docs/
                if (!trimmedRelPath.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
                {
                    trimmedRelPath = "docs/" + trimmedRelPath.TrimStart('/');
                }
                var externalDoc = await CreateExternalDocumentAsync(trimmedRelPath, content, docKind, topic, statusValue, order, actor);
                return DocSaveResponse.From(externalDoc, "created");
            }

            // Create managed document
            var doc = await _service.CreateDocumentAsync(trimmedRelPath, content, docKind, topic, statusValue, order ?? 0);

            _logger?.LogInformation("doc_save created managed doc '{Id}' at '{RelPath}'", doc.Id.Value, doc.RelPath);
            return DocSaveResponse.From(doc, "created");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("external"))
        {
            _logger?.LogWarning("doc_save refused external: {Message}", ex.Message);
            throw new InvalidOperationException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_save threw (relPath={RelPath})", relPath);
            throw;
        }
    }

    [McpServerTool(Name = "doc_annotate"), Description("Annotate a section of a managed document with status and/or annotation. Refused for external documents.")]
    public async Task<DocAnnotateResponse> AnnotateAsync(
        [Description("Document ID (ownership:origin:sha1).")] string docId,
        [Description("Section anchor (e.g. 'section-1' or '_preamble').")] string anchor,
        [Description("New status value (per-kind enum).")] int? status = null,
        [Description("Annotation text (supersession pointer, note, etc.).")] string? annotation = null,
        [Description("Actor for audit trail. Default 'mcp:doc_annotate'.")] string actor = "mcp:doc_annotate")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(docId))
            {
                throw new ArgumentException("docId cannot be empty.", nameof(docId));
            }

            if (string.IsNullOrWhiteSpace(anchor))
            {
                throw new ArgumentException("anchor cannot be empty.", nameof(anchor));
            }

            var id = DocId.Parse(docId);
            var success = await _service.AnnotateSectionAsync(id.Value, anchor.Trim(), status ?? 0, annotation?.Trim(), actor);

            _logger?.LogInformation("doc_annotate {Success} for '{DocId}' anchor '{Anchor}'", success, docId, anchor);
            return new DocAnnotateResponse(success);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("external"))
        {
            _logger?.LogWarning("doc_annotate refused external: {Message}", ex.Message);
            throw new InvalidOperationException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_annotate threw (docId={DocId})", docId);
            throw;
        }
    }

    [McpServerTool(Name = "doc_promote"), Description("Promote a managed document to external (writes to docs/). One-way operation. Refused if already external.")]
    public async Task<DocPromoteResponse> PromoteAsync(
        [Description("Document ID (ownership:origin:sha1).")] string docId,
        [Description("Actor for audit trail. Default 'mcp:doc_promote'.")] string actor = "mcp:doc_promote")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(docId))
            {
                throw new ArgumentException("docId cannot be empty.", nameof(docId));
            }

            var id = DocId.Parse(docId);
            var promoted = await _service.PromoteDocumentAsync(id.Value, actor);

            if (promoted == null)
            {
                throw new InvalidOperationException("Document not found or already external.");
            }

            _logger?.LogInformation("doc_promote '{DocId}' -> external at '{RelPath}'", docId, promoted.RelPath);
            return DocPromoteResponse.From(promoted);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("external"))
        {
            _logger?.LogWarning("doc_promote refused: {Message}", ex.Message);
            throw new InvalidOperationException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_promote threw (docId={DocId})", docId);
            throw;
        }
    }

    [McpServerTool(Name = "doc_relation"), Description("Create a relation between two managed documents (Implements, Supersedes, References). Write only between managed docs. Read with doc_relation tool.")]
    public async Task<DocRelationResponse> AddRelationAsync(
        [Description("Source document ID.")] string sourceId,
        [Description("Target document ID.")] string targetId,
        [Description("Relation kind: implements, supersedes, references.")] string kind,
        [Description("Actor for audit trail. Default 'mcp:doc_relation'.")] string actor = "mcp:doc_relation")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                throw new ArgumentException("sourceId cannot be empty.", nameof(sourceId));
            }
            if (string.IsNullOrWhiteSpace(targetId))
            {
                throw new ArgumentException("targetId cannot be empty.", nameof(targetId));
            }
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new ArgumentException("kind cannot be empty.", nameof(kind));
            }

            var source = DocId.Parse(sourceId);
            var target = DocId.Parse(targetId);
            var relationKind = ParseRelationKind(kind);

            await _service.AddRelationAsync(source.Value, target.Value, relationKind, actor);

            _logger?.LogInformation("doc_relation added {Kind} {Source} -> {Target}", kind, sourceId, targetId);
            return new DocRelationResponse(true);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("external"))
        {
            _logger?.LogWarning("doc_relation refused external: {Message}", ex.Message);
            throw new InvalidOperationException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_relation threw (source={Source}, target={Target})", sourceId, targetId);
            throw;
        }
    }

    [McpServerTool(Name = "doc_index"), Description("Trigger incremental rescan within allowed scope (respects coverage policy and decision). Returns indexed/skipped/deleted counts.")]
    public async Task<DocIndexResponse> IndexAsync(
        [Description("Full rescan (ignore hashes). Default false.")] bool full = false,
        [Description("Specific paths to scan (relative). Default all covered paths.")] string[]? paths = null)
    {
        try
        {
            var result = await _service.ScanAsync(full, paths?.Select(p => p.Trim().Replace('\\', '/')).ToList());

            _logger?.LogInformation("doc_index {Full} indexed={Indexed} skipped={Skipped} deleted={Deleted} errors={Errors}", full, result.Indexed, result.Skipped, result.Deleted, result.Errors);
            return DocIndexResponse.From(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_index threw (full={Full})", full);
            throw;
        }
    }

    [McpServerTool(Name = "doc_scope_policy"), Description("Inspect or change coverage policy (include/exclude patterns) and decision (ask/disabled/clear). Include patterns require approval.")]
    public async Task<ScopePolicyResponse> ScopePolicyAsync(
        [Description("Action: get, set. Default get.")] string action = "get",
        [Description("Include patterns (e.g. ['docs/**', '.eling/docs/**']).")] string[]? include = null,
        [Description("Exclude patterns (e.g. ['**/node_modules/**']).")] string[]? exclude = null,
        [Description("Decision: ask, disabled, clear.")] string? decision = null,
        [Description("Approve include pattern change (required when modifying include).")] bool approve = false)
    {
        try
        {
            var trimmedAction = action.Trim().ToLowerInvariant();

            if (trimmedAction == "get")
            {
                // Get current policy from index
                var policy = await _service.GetCoveragePolicyAsync();
                return ScopePolicyResponse.From(policy);
            }

            if (trimmedAction == "set")
            {
                if (include != null && !approve)
                {
                    throw new InvalidOperationException("Changing include patterns requires approve=true.");
                }

                var newPolicy = new CoveragePolicy
                {
                    Include = include?.Select(p => p.Trim().Replace('\\', '/')).ToArray() ?? Array.Empty<string>(),
                    Exclude = exclude?.Select(p => p.Trim().Replace('\\', '/')).ToArray() ?? Array.Empty<string>()
                };

                await _service.SetCoveragePolicyAsync(newPolicy, decision?.Trim().ToLowerInvariant());

                _logger?.LogInformation("doc_scope_policy set include={Include} exclude={Exclude} decision={Decision}", 
                    string.Join(",", newPolicy.Include), string.Join(",", newPolicy.Exclude), decision ?? "none");

                return ScopePolicyResponse.From(newPolicy, decision);
            }

            throw new ArgumentException($"Invalid action '{action}'. Use 'get' or 'set'.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_scope_policy threw (action={Action})", action);
            throw;
        }
    }

    [McpServerTool(Name = "doc_offer_managed"), Description("Read or answer the managed docs offer policy. States: unasked, accepted, declined, never. Re-offers on 30-day or 5-spec trigger when declined.")]
    public async Task<OfferResponse> OfferManagedAsync(
        [Description("Action: check, answer. Default check.")] string action = "check",
        [Description("Answer when action=answer: accepted, declined, never.")] string? answer = null)
    {
        try
        {
            var trimmedAction = action.Trim().ToLowerInvariant();

            if (trimmedAction == "check")
            {
                var result = await _service.CheckOfferPolicyAsync();
                return OfferResponse.FromCheck(result);
            }

            if (trimmedAction == "answer")
            {
                if (string.IsNullOrWhiteSpace(answer))
                {
                    throw new ArgumentException("answer is required when action=answer.", nameof(answer));
                }

                var answerState = ParseOfferState(answer);
                await _service.AnswerOfferAsync(answerState);

                _logger?.LogInformation("doc_offer_managed answered '{Answer}'", answer);
                return OfferResponse.FromAnswer(answerState);
            }

            throw new ArgumentException($"Invalid action '{action}'. Use 'check' or 'answer'.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "doc_offer_managed threw (action={Action})", action);
            throw;
        }
    }

    private async Task<Eling.Core.Docs.Doc> CreateExternalDocumentAsync(
        string relPath,
        string content,
        DocKind kind,
        string? topic,
        DocStatusValue? status,
        int? order,
        string actor)
    {
        // This is a simplified external creation - in reality would use the service's promote logic
        // For now, we'll use the index directly since it's external
        var fileHash = _service.ComputeHash(content);
        var mtime = DateTimeOffset.UtcNow;
        
        // We'll create a minimal external doc
        var resolution = new DocScopeResolution(
            DocOwnership.External,
            DocOrigin.Current,
            relPath,
            relPath); // root path not used for external

        var inferredTopic = topic ?? InferTopicFromPath(relPath);
        var inferredStatus = status ?? DocStatusValue.DefaultFor(kind);
        var inferredOrder = order ?? InferOrderFromFilename(relPath);
        var title = ExtractTitle(content) ?? Path.GetFileNameWithoutExtension(relPath);

        var docId = DocId.Create(DocOwnership.External, DocOrigin.Current, relPath);
        var doc = new Eling.Core.Docs.Doc(
            docId,
            DocOwnership.External,
            DocOrigin.Current,
            relPath,
            kind,
            title,
            inferredStatus,
            inferredTopic,
            inferredOrder,
            null,
            0, 0,
            fileHash,
            mtime);

        // Write file to docs/
        var absolutePath = Path.Combine(_service.GetWorkspaceRoot(), relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, content);

        // Index it
        var sections = new List<Eling.Core.Docs.DocSection>();
        var refs = new List<Eling.Core.Docs.DocRef>();
        
        await _service.IndexExternalAsync(doc, sections, refs);

        var afterJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            doc.Id,
            doc.Ownership,
            doc.Origin,
            doc.RelPath,
            doc.Kind,
            doc.Title,
            doc.Status,
            doc.Topic,
            doc.Order,
            doc.SupersededBy,
            doc.StepsTotal,
            doc.StepsDone,
            doc.FileHash,
            doc.MTime
        });

        await _service.WriteAuditAsync(doc.Id.Value, "promote", null, afterJson, actor);

        return doc;
    }

    private static DocKind ParseKindOrDefault(string? kind, string relPath)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            return kind.Trim().ToLowerInvariant() switch
            {
                "spec" => DocKind.Spec,
                "plan" => DocKind.Plan,
                "doc" => DocKind.Doc,
                "scratch" => DocKind.Scratch,
                _ => throw new ArgumentException($"Invalid kind '{kind}'. Use 'spec', 'plan', 'doc', or 'scratch'.")
            };
        }

        var lower = relPath.ToLowerInvariant();
        if (lower.Contains("/specs/") || lower.StartsWith("specs/")) return DocKind.Spec;
        if (lower.Contains("/plans/") || lower.StartsWith("plans/")) return DocKind.Plan;
        if (lower.Contains("/docs/") || lower.StartsWith("docs/")) return DocKind.Doc;
        return DocKind.Scratch;
    }

    private static RelationKind ParseRelationKind(string kind)
    {
        return kind.Trim().ToLowerInvariant() switch
        {
            "implements" => RelationKind.Implements,
            "supersedes" => RelationKind.Supersedes,
            "references" => RelationKind.References,
            _ => throw new ArgumentException($"Invalid relation kind '{kind}'. Use 'implements', 'supersedes', or 'references'.")
        };
    }

    private static OfferState ParseOfferState(string answer)
    {
        return answer.Trim().ToLowerInvariant() switch
        {
            "accepted" => OfferState.Accepted,
            "declined" => OfferState.Declined,
            "never" => OfferState.Never,
            _ => throw new ArgumentException($"Invalid answer '{answer}'. Use 'accepted', 'declined', or 'never'.")
        };
    }

    private static string InferTopicFromPath(string relPath)
    {
        var parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return parts[parts.Length - 2];
        }
        return "general";
    }

    private static int InferOrderFromFilename(string relPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(relPath);
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var order))
        {
            return order;
        }
        return 0;
    }

    private static string? ExtractTitle(string content)
    {
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# "))
            {
                return trimmed.Substring(2).Trim();
            }
        }
        return null;
    }
}