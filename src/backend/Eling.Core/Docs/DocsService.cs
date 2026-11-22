namespace Eling.Core.Docs;

using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Eling.Core.Codebase;
using Eling.Core.Scope;

/// <summary>
/// High-level service for document indexing, scanning, and lifecycle management.
/// Coordinates DocScopeResolver, SqliteDocsIndex, front-matter parsing, reference resolution, and audit.
/// </summary>
public sealed class DocsService
{
    private readonly IDocsIndex _docsCurrentIndex;
    private readonly IDocsIndex _docsShardIndex;
    private readonly IDocsIndex _docsGlobalIndex;
    private readonly DocScopeResolver _docResolver;
    private readonly ICodebaseIndex? _codebaseIndex;
    private readonly ILogger<DocsService> _logger;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public DocsService(
        [FromKeyedServices("current")] IDocsIndex currentIndex,
        [FromKeyedServices("shard")] IDocsIndex shardIndex,
        [FromKeyedServices("global")] IDocsIndex globalIndex,
        DocScopeResolver scopeResolver,
        ICodebaseIndex? codebaseIndex = null,
        ILogger<DocsService>? logger = null)
    {
        _docsCurrentIndex = currentIndex ?? throw new ArgumentNullException(nameof(currentIndex));
        _docsShardIndex = shardIndex ?? throw new ArgumentNullException(nameof(shardIndex));
        _docsGlobalIndex = globalIndex ?? throw new ArgumentNullException(nameof(globalIndex));
        _docResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _codebaseIndex = codebaseIndex;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DocsService>.Instance;
    }

    /// <summary>
    /// Gets the index for the given origin, respecting the scope decision.
    /// When decision is Disabled, Current origin routes to Shard.
    /// </summary>
    private async Task<IDocsIndex> GetIndexForOriginAsync(DocOrigin origin, CancellationToken ct = default)
    {
        if (origin == DocOrigin.Current)
        {
            var decision = await GetScopeDecisionAsync(ct);
            if (decision == ScopeDecision.Disabled)
            {
                return _docsShardIndex;
            }
            return _docsCurrentIndex;
        }
        return origin switch
        {
            DocOrigin.Shard => _docsShardIndex,
            DocOrigin.Global => _docsGlobalIndex,
            DocOrigin.Ancestor => _docsShardIndex, // Ancestor uses shard for now
            _ => _docsShardIndex
        };
    }

    /// <summary>
    /// Gets the index for a document's origin.
    /// </summary>
    private async Task<IDocsIndex> GetIndexForDocAsync(Doc doc, CancellationToken ct = default)
        => await GetIndexForOriginAsync(doc.Origin, ct);

    /// <summary>
    /// Scans the workspace for documents and indexes them incrementally.
    /// Skips files whose hash matches the stored hash.
    /// </summary>
    public async Task<ScanResult> ScanAsync(
        bool full = false,
        IEnumerable<string>? paths = null,
        CancellationToken ct = default)
    {
        if (!await _scanGate.WaitAsync(0, ct))
        {
            return new ScanResult(0, 0, 0, 0, 0, true);
        }

        try
        {
            var workspaceRoot = _docResolver.WorkspaceRoot;
            var coverage = await GetEffectiveCoverageAsync(ct);

            // Determine files to scan
            var filesToScan = new List<string>();
            if (paths?.Any() == true)
            {
                filesToScan.AddRange(paths.Where(p => File.Exists(p) && IsCovered(p, coverage, workspaceRoot)));
            }
            else
            {
                // Full workspace scan
                filesToScan.AddRange(EnumerateCandidateFiles(workspaceRoot, coverage));
            }

            var indexed = 0;
            var skipped = 0;
            var deleted = 0;
            var errors = 0;
            var errorMessages = new List<string>();

            // Get existing indexed docs for deleted file detection
            var existingDocs = new HashSet<string>();
            if (!full)
            {
                var index = await GetIndexForOriginAsync(DocOrigin.Current, ct);
                var allDocs = await index.ListAsync();
                foreach (var doc in allDocs)
                {
                    existingDocs.Add(doc.RelPath);
                }
            }

            // Process each file
            foreach (var file in filesToScan)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var relPath = GetRelativePath(file, workspaceRoot);
                    var fileHash = ComputeFileHash(file);
                    var fileInfo = new FileInfo(file);
                    var mtime = new DateTimeOffset(fileInfo.LastWriteTimeUtc);

                    // Check if already indexed with same hash
                    if (!full)
                    {
                        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct);
                        var existing = await index.GetAsync(DocId.Create(DocOwnership.Managed, DocOrigin.Current, relPath).Value);
                        if (existing?.FileHash == fileHash)
                        {
                            skipped++;
                            existingDocs.Remove(relPath);
                            continue;
                        }
                    }

                    // Parse document
                    var content = await File.ReadAllTextAsync(file, ct);
                    var parseResult = await ParseDocumentAsync(file, relPath, content, fileHash, mtime, ct);
                    var doc = parseResult.Doc;
                    var sections = parseResult.Sections;
                    var refs = parseResult.Refs;

                    if (doc != null)
                    {
                        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct);
                        await index.IndexAsync(doc, sections, refs);
                        indexed++;
                        existingDocs.Remove(relPath);
                    }
                    else
                    {
                        errors++;
                        errorMessages.Add($"Failed to parse {relPath}");
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    errorMessages.Add($"Error indexing {file}: {ex.Message}");
                    _logger.LogWarning(ex, "Error indexing file {File}", file);
                }
            }

            // Handle deleted files (only in non-full scan)
            if (!full)
            {
                foreach (var deletedRelPath in existingDocs)
                {
                    var docId = DocId.Create(DocOwnership.Managed, DocOrigin.Current, deletedRelPath).Value;
                    var index = await GetIndexForOriginAsync(DocOrigin.Current, ct);
                    await index.DeleteAsync(docId);
                    deleted++;
                }
            }

            return new ScanResult(indexed, skipped, deleted, errors, filesToScan.Count, false, errorMessages);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// Parses a document file into Doc, sections, and references.
    /// </summary>
    public async Task<DocParseResult> ParseDocumentAsync(
        string absolutePath,
        string relPath,
        string content,
        string fileHash,
        DateTimeOffset mtime,
        CancellationToken ct = default)
    {
        var resolution = _docResolver.Resolve(absolutePath);

        // Parse front-matter if managed
        DocFrontMatter? frontMatter = null;
        string remainingContent = content;
        bool frontMatterError = false;
        string? frontMatterErrorMsg = null;

        if (resolution.Ownership == DocOwnership.Managed)
        {
            var parseResult = DocFrontMatterParser.Parse(content, _logger);
            frontMatter = parseResult.FrontMatter;
            remainingContent = parseResult.RemainingContent;
            frontMatterError = parseResult.ErrorMessage != null;
            frontMatterErrorMsg = parseResult.ErrorMessage;
        }

        // Determine kind, topic, status, order
        DocKind kind;
        string topic;
        DocStatusValue status;
        int order;
        string? supersededBy = null;
        int stepsTotal = 0;
        int stepsDone = 0;

        if (frontMatter != null)
        {
            kind = frontMatter.Kind ?? InferKindFromPath(relPath);
            topic = frontMatter.Topic ?? InferTopicFromPath(relPath);
            status = frontMatter.Status.HasValue
                ? new DocStatusValue(kind, frontMatter.Status.Value)
                : DocStatusValue.DefaultFor(kind);
            order = frontMatter.Order ?? InferOrderFromFilename(relPath);
            supersededBy = frontMatter.SupersededBy;
        }
        else
        {
            // External or managed without front-matter: infer everything
            kind = InferKindFromPath(relPath);
            topic = InferTopicFromPath(relPath);
            status = DocStatusValue.DefaultFor(kind);
            order = InferOrderFromFilename(relPath);
        }

        // Extract title from first heading
        var title = ExtractTitle(remainingContent) ?? Path.GetFileNameWithoutExtension(relPath);

        // Chunk sections
        var chunkResult = HeadingChunker.Chunk(remainingContent);
        var sectionChunks = chunkResult.Sections;
        var preamble = chunkResult.Preamble;

        // Convert SectionChunk to DocSection
        var sections = new List<DocSection>();

        // Add preamble as first section if not empty
        if (!string.IsNullOrWhiteSpace(preamble))
        {
            sections.Add(new DocSection(
                DocId.Empty, // Will be updated after doc creation
                "_preamble",
                "Preamble",
                0,
                1,
                preamble.Split('\n').Length,
                DocStatusValue.DefaultFor(kind)));
        }

        // Add heading sections
        foreach (var chunk in sectionChunks)
        {
            sections.Add(new DocSection(
                DocId.Empty, // Will be updated after doc creation
                chunk.Anchor,
                chunk.RawHeadingText,
                chunk.Level,
                chunk.LineStart,
                chunk.LineEnd,
                DocStatusValue.DefaultFor(kind)));
        }

        // Count checkboxes for plans
        if (kind == DocKind.Plan)
        {
            var checkboxCounts = PlanCheckboxCounter.Count(remainingContent);
            stepsTotal = checkboxCounts.Total;
            stepsDone = checkboxCounts.Done;
            // Also count per section
            var perSection = PlanCheckboxCounter.CountPerSection(remainingContent, sectionChunks);
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (perSection.TryGetValue(section.Anchor, out var counts))
                {
                    // Update section with step counts (section is mutable for steps)
                    section.StepsTotal = counts.Total;
                    section.StepsDone = counts.Done;
                }
            }
        }

        // Extract references (backticks)
        var refs = ExtractReferences(remainingContent, sections);

        // Resolve references against codebase index
        if (_codebaseIndex != null)
        {
            refs = await ResolveReferencesAsync(refs, ct);
        }

        // Create Doc entity
        var docId = DocId.Create(resolution.Ownership, resolution.Origin, relPath);
        var doc = new Doc(
            docId,
            resolution.Ownership,
            resolution.Origin,
            relPath,
            kind,
            title,
            status,
            topic,
            order,
            supersededBy,
            stepsTotal,
            stepsDone,
            fileHash,
            mtime,
            frontMatterError: frontMatterError,
            frontMatterErrorMessage: frontMatterErrorMsg);

        // Update section doc_ids
        var updatedSections = sections.Select(s =>
        {
            // Create new DocSection with correct DocId
            return new DocSection(
                docId,
                s.Anchor,
                s.Heading,
                s.Level,
                s.LineStart,
                s.LineEnd,
                s.Status,
                s.Annotation,
                s.StepsTotal,
                s.StepsDone);
        }).ToList();

        return new DocParseResult(doc, updatedSections, refs);
    }

    /// <summary>
    /// Creates a new managed document.
    /// </summary>
    public async Task<Doc> CreateDocumentAsync(
        string relPath,
        string content,
        DocKind kind,
        string? topic = null,
        DocStatusValue? status = null,
        int order = 0,
        CancellationToken ct = default)
    {
        var workspaceRoot = _docResolver.WorkspaceRoot;
        var absolutePath = Path.Combine(workspaceRoot, relPath);
        var resolution = _docResolver.Resolve(absolutePath);

        if (resolution.Ownership != DocOwnership.Managed)
        {
            throw new InvalidOperationException($"Cannot create document at {relPath}: path is not in managed scope");
        }

        var fileHash = ComputeHash(content);
        var mtime = DateTimeOffset.UtcNow;

        var parseResult = await ParseDocumentAsync(absolutePath, relPath, content, fileHash, mtime, ct);
        var doc = parseResult.Doc;
        var sections = parseResult.Sections;
        var refs = parseResult.Refs;
        if (doc == null) throw new InvalidOperationException("Failed to parse document");

        var index = await GetIndexForOriginAsync(resolution.Origin, ct);
        await index.IndexAsync(doc, sections, refs);
        await WriteAuditDocAsync(doc.Id.Value, "create", null, doc, "docs_service");

        return doc;
    }

    /// <summary>
    /// Updates an existing managed document.
    /// </summary>
    public async Task<Doc?> UpdateDocumentAsync(
        string docId,
        string content,
        CancellationToken ct = default)
    {
        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct); // First get any index to retrieve the doc
        var existing = await index.GetAsync(docId);
        if (existing == null) return null;

        if (existing.IsExternal)
        {
            throw new InvalidOperationException("Cannot update external document");
        }

        var beforeJson = SerializeDoc(existing);

        var fileHash = ComputeHash(content);
        var mtime = DateTimeOffset.UtcNow;
        var absolutePath = Path.Combine(_docResolver.WorkspaceRoot, existing.RelPath);

        var parseResult = await ParseDocumentAsync(absolutePath, existing.RelPath, content, fileHash, mtime, ct);
        var doc = parseResult.Doc;
        var sections = parseResult.Sections;
        var refs = parseResult.Refs;
        if (doc == null) return null;

        // Create new doc with same ID
        var updatedDoc = new Doc(
            existing.Id,
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
            doc.MTime,
            doc.IndexedAt,
            doc.FrontMatterError,
            doc.FrontMatterErrorMessage);

        var writeIndex = await GetIndexForDocAsync(existing, ct);
        await writeIndex.IndexAsync(updatedDoc, sections, refs);
        await WriteAuditDocAsync(docId, "update", beforeJson, updatedDoc, "docs_service");

        return updatedDoc;
    }

    /// <summary>
    /// Archives (soft deletes) a managed document.
    /// </summary>
    public async Task<bool> ArchiveDocumentAsync(string docId, string actor = "docs_service", CancellationToken ct = default)
    {
        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct); // First get any index to retrieve the doc
        var existing = await index.GetAsync(docId);
        if (existing == null) return false;

        if (existing.IsExternal)
        {
            throw new InvalidOperationException("Cannot archive external document");
        }

        var beforeJson = SerializeDoc(existing);
        var writeIndex = await GetIndexForDocAsync(existing, ct);
        await writeIndex.DeleteAsync(docId);
        await WriteAuditAsync(docId, "delete", beforeJson, null, actor);

        return true;
    }

    /// <summary>
    /// Promotes a managed document to external (writes to docs/).
    /// </summary>
    public async Task<Doc?> PromoteDocumentAsync(string docId, string actor = "docs_service", CancellationToken ct = default)
    {
        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct); // First get any index to retrieve the doc
        var existing = await index.GetAsync(docId);
        if (existing == null) return null;

        if (existing.IsExternal)
        {
            throw new InvalidOperationException("Document is already external");
        }

        // Read current content
        var absolutePath = Path.Combine(_docResolver.WorkspaceRoot, existing.RelPath);
        var content = await File.ReadAllTextAsync(absolutePath, ct);

        // Determine target path in docs/
        var targetRelPath = existing.RelPath;
        if (!targetRelPath.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            targetRelPath = Path.Combine("docs", targetRelPath).Replace('\\', '/');
        }

        var targetAbsolutePath = Path.Combine(_docResolver.WorkspaceRoot, targetRelPath);

        // Ensure directory exists
        var targetDir = Path.GetDirectoryName(targetAbsolutePath)!;
        Directory.CreateDirectory(targetDir);

        // Write to docs/
        await File.WriteAllTextAsync(targetAbsolutePath, content, ct);

        // Update index: mark as external
        var beforeJson = SerializeDoc(existing);
        var promotedDoc = new Doc(
            DocId.Create(DocOwnership.External, DocOrigin.Current, targetRelPath),
            DocOwnership.External,
            DocOrigin.Current,
            targetRelPath,
            existing.Kind,
            existing.Title,
            existing.Status,
            existing.Topic,
            existing.Order,
            existing.SupersededBy,
            existing.StepsTotal,
            existing.StepsDone,
            existing.FileHash,
            existing.MTime,
            existing.IndexedAt,
            existing.FrontMatterError,
            existing.FrontMatterErrorMessage);

        var writeIndex = await GetIndexForDocAsync(existing, ct);
        await writeIndex.DeleteAsync(docId);
        var sections = await writeIndex.GetSectionsAsync(docId);
        var refs = await writeIndex.GetRefsAsync(docId);
        await writeIndex.IndexAsync(promotedDoc, sections, refs);

        var afterJson = SerializeDoc(promotedDoc);
        await WriteAuditAsync(docId, "promote", beforeJson, afterJson, actor);

        return promotedDoc;
    }

    /// <summary>
    /// Annotates a section of a managed document.
    /// </summary>
    public async Task<bool> AnnotateSectionAsync(
        string docId,
        string anchor,
        int status,
        string? annotation,
        string actor = "docs_service",
        CancellationToken ct = default)
    {
        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct);
        var existing = await index.GetAsync(docId);
        if (existing == null) return false;

        if (existing.IsExternal)
        {
            throw new InvalidOperationException("Cannot annotate external document");
        }

        var section = await index.GetSectionAsync(docId, anchor);
        if (section == null) return false;

        var statusValue = new DocStatusValue(existing.Kind, status);
        var beforeJson = SerializeSection(section);
        var updatedSection = new DocSection(
            section.DocId,
            section.Anchor,
            section.Heading,
            section.Level,
            section.LineStart,
            section.LineEnd,
            statusValue,
            annotation,
            section.StepsTotal,
            section.StepsDone);

        // Re-index the whole doc to update the section
        var allSections = await index.GetSectionsAsync(docId);
        var updatedSections = allSections.Select(s => s.Anchor == anchor ? updatedSection : s).ToList();

        var writeIndex = await GetIndexForDocAsync(existing, ct);
        await writeIndex.IndexAsync(existing, updatedSections, await writeIndex.GetRefsAsync(docId));

        var afterJson = SerializeSection(updatedSection);
        await WriteAuditAsync(docId, "annotate", beforeJson, afterJson, actor);

        return true;
    }

    /// <summary>
    /// Adds a relation between two managed documents.
    /// </summary>
    public async Task AddRelationAsync(
        string sourceId,
        string targetId,
        RelationKind kind,
        string actor = "docs_service",
        CancellationToken ct = default)
    {
        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct); // First get any index to retrieve the docs
        var source = await index.GetAsync(sourceId);
        var target = await index.GetAsync(targetId);

        if (source == null || target == null)
        {
            throw new InvalidOperationException("Source or target document not found");
        }

        if (source.IsExternal || target.IsExternal)
        {
            throw new InvalidOperationException("Cannot create relation involving external document");
        }

        var relation = new DocRelation(
            DocId.Parse(sourceId),
            DocId.Parse(targetId),
            kind);

        var writeIndex = await GetIndexForDocAsync(source, ct);
        await writeIndex.UpsertRelationAsync(relation);
        await WriteAuditAsync(sourceId, "relate", null, $"relation:{kind}:{targetId}", actor);
    }

    /// <summary>
    /// Generates a staleness report for documents.
    /// </summary>
    public async Task<StalenessReport> GetStalenessReportAsync(CancellationToken ct = default)
    {
        // Use shard index which contains all managed documents
        var allDocs = await _docsShardIndex.ListAsync();
        var findings = new List<StalenessFinding>();

        foreach (var docItem in allDocs)
        {
            var doc = await _docsShardIndex.GetAsync(docItem.DocId);
            if (doc == null) continue;

            // Check unresolved references
            var refs = await _docsShardIndex.GetRefsAsync(doc.Id.Value);
            foreach (var refItem in refs)
            {
                if (!refItem.Resolved)
                {
                    findings.Add(new StalenessFinding(
                        doc.Id.Value,
                        doc.Title,
                        doc.RelPath,
                        StalenessKind.UnresolvedReference,
                        $"Unresolved reference: {refItem.RawText} in section {refItem.Anchor}",
                        refItem.Anchor));
                }
            }

            // Check superseded without replacement
            if (!string.IsNullOrEmpty(doc.SupersededBy))
            {
                var replacement = await _docsShardIndex.GetAsync(DocId.Create(doc.Ownership, doc.Origin, doc.SupersededBy!).Value);
                if (replacement == null)
                {
                    findings.Add(new StalenessFinding(
                        doc.Id.Value,
                        doc.Title,
                        doc.RelPath,
                        StalenessKind.SupersededWithoutReplacement,
                        $"Superseded by {doc.SupersededBy} but replacement not found",
                        null));
                }
            }

            // Check stale draft (> 30 days)
            if (doc.Kind == DocKind.Spec && doc.Status.Value == (int)SpecStatus.Draft)
            {
                if (DateTimeOffset.UtcNow - doc.MTime > TimeSpan.FromDays(30))
                {
                    findings.Add(new StalenessFinding(
                        doc.Id.Value,
                        doc.Title,
                        doc.RelPath,
                        StalenessKind.StaleDraft,
                        "Draft spec older than 30 days",
                        null));
                }
            }

            // Check untouched (> 180 days)
            if (DateTimeOffset.UtcNow - doc.MTime > TimeSpan.FromDays(180))
            {
                findings.Add(new StalenessFinding(
                    doc.Id.Value,
                    doc.Title,
                    doc.RelPath,
                    StalenessKind.Untouched,
                    "Document untouched for > 180 days",
                    null));
            }
        }

        return new StalenessReport(findings, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Gets audit entries for a document or scope.
    /// </summary>
    public async Task<IReadOnlyList<DocAuditEntry>> GetAuditAsync(string? docId = null, int limit = 100, CancellationToken ct = default)
    {
        // Use shard index for audit (contains all managed docs)
        return await _docsShardIndex.GetAuditAsync(docId, limit);
    }

    /// <summary>
    /// Checks and potentially triggers the managed docs offer policy.
    /// </summary>
    public async Task<OfferCheckResult> CheckOfferPolicyAsync(CancellationToken ct = default)
    {
        // Check scope decision first - silent under disabled
        var decision = await GetScopeDecisionAsync(ct);
        if (decision == ScopeDecision.Disabled)
        {
            return new OfferCheckResult(false, OfferState.Never, "Scope decision is disabled");
        }

        var projectHash = ComputeProjectHash(_docResolver.CanonicalRoot);
        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct);
        var offerState = await index.GetOfferStateAsync(projectHash);

        if (offerState == null)
        {
            // First time - create unasked state
            offerState = new DocOfferState(projectHash, OfferState.Unasked, null, null, 0);
            await index.SetOfferStateAsync(offerState);
            return new OfferCheckResult(true, OfferState.Unasked, "Offer available");
        }

        if (offerState.State == OfferState.Accepted || offerState.State == OfferState.Never)
        {
            return new OfferCheckResult(false, offerState.State, "Offer already decided");
        }

        if (offerState.State == OfferState.Declined)
        {
            // Check re-offer triggers
            var shouldReoffer = false;
            var reason = "";

            if (offerState.AskedAt.HasValue && DateTimeOffset.UtcNow - offerState.AskedAt.Value > TimeSpan.FromDays(30))
            {
                shouldReoffer = true;
                reason = "30 days since decline";
            }
            else if (offerState.SpecsSinceAsk >= 5)
            {
                shouldReoffer = true;
                reason = "5 specs/plans written since decline";
            }

            if (shouldReoffer)
            {
                offerState = new DocOfferState(projectHash, OfferState.Unasked, null, null, 0);
                await index.SetOfferStateAsync(offerState);
                return new OfferCheckResult(true, OfferState.Unasked, reason);
            }

            return new OfferCheckResult(false, offerState.State, "Declined, re-offer triggers not met");
        }

        // Unasked - offer it
        return new OfferCheckResult(true, OfferState.Unasked, "Offer available");
    }

    /// <summary>
    /// Answers the managed docs offer.
    /// </summary>
    public async Task AnswerOfferAsync(OfferState answer, CancellationToken ct = default)
    {
        var projectHash = ComputeProjectHash(_docResolver.CanonicalRoot);
        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct);
        var offerState = await index.GetOfferStateAsync(projectHash) ?? new DocOfferState(projectHash, OfferState.Unasked, null, null, 0);

        var newState = new DocOfferState(
            projectHash,
            answer,
            offerState.AskedAt ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0);

        await index.SetOfferStateAsync(newState);
    }

    /// <summary>
    /// Increments the specs counter for offer re-trigger.
    /// </summary>
    public async Task IncrementSpecsCounterAsync(CancellationToken ct = default)
    {
        var projectHash = ComputeProjectHash(_docResolver.CanonicalRoot);
        var index = await GetIndexForOriginAsync(DocOrigin.Current, ct);
        var offerState = await index.GetOfferStateAsync(projectHash);
        if (offerState != null && offerState.State == OfferState.Declined)
        {
            var newState = new DocOfferState(
                projectHash,
                offerState.State,
                offerState.AskedAt,
                offerState.AnsweredAt,
                offerState.SpecsSinceAsk + 1);

            await index.SetOfferStateAsync(newState);
        }
    }

    /// <summary>
    /// Gets the current scope decision (ask/disabled/clear).
    /// </summary>
    public async Task<ScopeDecision> GetScopeDecisionAsync(CancellationToken ct = default)
    {
        // Use shard index for policy (always available)
        var policy = await _docsShardIndex.GetPolicyAsync("decision");
        if (policy != null)
        {
            try
            {
                var decision = policy.Value.Trim().ToLowerInvariant();
                return decision switch
                {
                    "disabled" => ScopeDecision.Disabled,
                    "clear" => ScopeDecision.Clear,
                    _ => ScopeDecision.Ask
                };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to deserialize scope decision, using defaults");
            }
        }
        return ScopeDecision.Ask;
    }

    // Private helper methods

    private async Task<CoveragePolicy> GetEffectiveCoverageAsync(CancellationToken ct)
    {
        // Use shard index for policy (always available)
        var policy = await _docsShardIndex.GetPolicyAsync("coverage");
        if (policy != null)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<CoveragePolicy>(policy.Value)!;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to deserialize coverage policy, using defaults");
            }
        }
        return new CoveragePolicy
        {
            Include = new[] { "docs/**", ".eling/docs/**" },
            Exclude = new[] { ".superpowers/sdd/**" }
        };
    }

    private bool IsCovered(string absolutePath, CoveragePolicy coverage, string workspaceRoot)
    {
        var relPath = GetRelativePath(absolutePath, workspaceRoot);
        var relPathUnix = relPath.Replace('\\', '/');

        // Check exclude first
        foreach (var pattern in coverage.Exclude)
        {
            if (MatchPattern(relPathUnix, pattern)) return false;
        }

        // Check include
        foreach (var pattern in coverage.Include)
        {
            if (MatchPattern(relPathUnix, pattern)) return true;
        }

        return false;
    }

    private bool MatchPattern(string path, string pattern)
    {
        // Simple glob matching
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase);
    }

    private IEnumerable<string> EnumerateCandidateFiles(string workspaceRoot, CoveragePolicy coverage)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in coverage.Include)
        {
            var searchPattern = pattern.Replace('/', Path.DirectorySeparatorChar);
            var fullPattern = Path.Combine(workspaceRoot, searchPattern);
            var dir = Path.GetDirectoryName(fullPattern) ?? workspaceRoot;
            var filePattern = Path.GetFileName(fullPattern);

            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, filePattern, SearchOption.AllDirectories))
                {
                    if (IsCovered(file, coverage, workspaceRoot))
                    {
                        files.Add(file);
                    }
                }
            }
        }

        return files;
    }

    private string GetRelativePath(string absolutePath, string root)
    {
        var fullPath = Path.GetFullPath(absolutePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = fullPath.Substring(fullRoot.Length + 1);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private string ComputeFileHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string ComputeProjectHash(string canonicalRoot)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(canonicalRoot);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private DocKind InferKindFromPath(string relPath)
    {
        var lower = relPath.ToLowerInvariant();
        if (lower.Contains("/specs/") || lower.StartsWith("specs/")) return DocKind.Spec;
        if (lower.Contains("/plans/") || lower.StartsWith("plans/")) return DocKind.Plan;
        if (lower.Contains("/docs/") || lower.StartsWith("docs/")) return DocKind.Doc;
        return DocKind.Scratch;
    }

    private string InferTopicFromPath(string relPath)
    {
        var parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return parts[parts.Length - 2];
        }
        return "general";
    }

    private int InferOrderFromFilename(string relPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(relPath);
        var match = Regex.Match(fileName, @"^(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var order))
        {
            return order;
        }
        return 0;
    }

    private string? ExtractTitle(string content)
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

    private List<DocRef> ExtractReferences(string content, IReadOnlyList<DocSection> sections)
    {
        var refs = new List<DocRef>();

        // Find all backtick-enclosed content
        var backtickRegex = new Regex(@"`([^`\n]+)`");
        var matches = backtickRegex.Matches(content);

        foreach (Match match in matches)
        {
            var rawText = match.Groups[1].Value;
            var lineNumber = content.Substring(0, match.Index).Count(c => c == '\n') + 1;

            // Find which section this reference belongs to
            var anchor = "_preamble";
            foreach (var section in sections)
            {
                if (lineNumber >= section.LineStart && lineNumber <= section.LineEnd)
                {
                    anchor = section.Anchor;
                    break;
                }
            }

            // Classify reference type
            var refKind = ClassifyReference(rawText);

            refs.Add(new DocRef(
                DocId.Create(DocOwnership.Managed, DocOrigin.Current, "placeholder"), // Will be set later
                anchor,
                rawText,
                refKind));
        }

        return refs;
    }

    private ReferenceKind ClassifyReference(string rawText)
    {
        // Path-like: contains / or \ or ends with .md/.cs/.ts etc
        if (rawText.Contains('/') || rawText.Contains('\\') ||
            rawText.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            rawText.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            rawText.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return ReferenceKind.Path;
        }

        // Anchor link: starts with #
        if (rawText.StartsWith('#'))
        {
            return ReferenceKind.AnchorLink;
        }

        // Symbol (default)
        return ReferenceKind.Symbol;
    }

    private async Task<List<DocRef>> ResolveReferencesAsync(List<DocRef> refs, CancellationToken ct)
    {
        var resolved = new List<DocRef>();

        foreach (var refItem in refs)
        {
            var resolvedRef = refItem;
            bool resolvedFlag = false;
            string? resolvedPath = null;
            int? resolvedLine = null;

            if (refItem.RefKind == ReferenceKind.Symbol && _codebaseIndex != null)
            {
                // Search for symbol in codebase
                var hits = await _codebaseIndex.SearchPorterAsync($"\"{refItem.RawText}\"", 1, ct: ct);
                if (hits.Count > 0)
                {
                    resolvedFlag = true;
                    resolvedPath = hits[0].Path;
                    resolvedLine = hits[0].StartLine;
                }
            }
            else if (refItem.RefKind == ReferenceKind.Path)
            {
                // Check if file exists
                var workspaceRoot = _docResolver.WorkspaceRoot;
                var fullPath = Path.Combine(workspaceRoot, refItem.RawText);
                if (File.Exists(fullPath))
                {
                    resolvedFlag = true;
                    resolvedPath = refItem.RawText;
                    resolvedLine = 1;
                }
            }

            resolvedRef = new DocRef(
                refItem.DocId,
                refItem.Anchor,
                refItem.RawText,
                refItem.RefKind)
            {
                Resolved = resolvedFlag,
                ResolvedPath = resolvedPath,
                ResolvedLine = resolvedLine,
                ResolvedAt = resolvedFlag ? DateTimeOffset.UtcNow : null
            };

            resolved.Add(resolvedRef);
        }

        return resolved;
    }

    private async Task WriteAuditDocAsync(
        string docId,
        string action,
        string? beforeJson,
        Doc? afterDoc,
        string actor,
        CancellationToken ct = default)
    {
        var afterJson = afterDoc != null ? SerializeDoc(afterDoc) : null;
        await WriteAuditAsync(docId, action, beforeJson, afterJson, actor, ct);
    }

    private string SerializeDoc(Doc doc)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
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
    }

    private string SerializeSection(DocSection section)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            section.DocId,
            section.Anchor,
            section.Heading,
            section.Level,
            section.LineStart,
            section.LineEnd,
            section.Status,
            section.Annotation
        });
    }

    /// <summary>
    /// Gets the current coverage policy from the index.
    /// </summary>
    public async Task<CoveragePolicy> GetCoveragePolicyAsync(CancellationToken ct = default)
    {
        // Use shard index for policy (always available)
        var policy = await _docsShardIndex.GetPolicyAsync("coverage");
        if (policy != null)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<CoveragePolicy>(policy.Value)!;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to deserialize coverage policy, using defaults");
            }
        }
        return new CoveragePolicy
        {
            Include = new[] { "docs/**", ".eling/docs/**" },
            Exclude = new[] { ".superpowers/sdd/**" }
        };
    }

    /// <summary>
    /// Sets the coverage policy and optional decision in the index.
    /// </summary>
    public async Task SetCoveragePolicyAsync(CoveragePolicy policy, ScopeDecision? decision = null, CancellationToken ct = default)
    {
        var policyJson = System.Text.Json.JsonSerializer.Serialize(policy);
        // Use shard index for policy (always available)
        await _docsShardIndex.SetPolicyAsync(new DocPolicy("coverage", policyJson, DateTimeOffset.UtcNow));

        if (decision.HasValue)
        {
            var decisionPolicy = new DocPolicy("decision", decision.Value.ToString().ToLowerInvariant(), DateTimeOffset.UtcNow);
            await _docsShardIndex.SetPolicyAsync(decisionPolicy);
        }
    }

    /// <summary>
    /// Indexes an external document (bypasses managed document checks).
    /// </summary>
    public async Task IndexExternalAsync(Doc doc, IReadOnlyList<DocSection> sections, IReadOnlyList<DocRef> refs, CancellationToken ct = default)
    {
        var index = await GetIndexForOriginAsync(doc.Origin, ct);
        await index.IndexAsync(doc, sections, refs);
    }

    /// <summary>
    /// Gets the workspace root directory.
    /// </summary>
    public string GetWorkspaceRoot() => _docResolver.WorkspaceRoot;

    /// <summary>
    /// Computes SHA256 hash of content.
    /// </summary>
    public string ComputeHash(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Writes an audit entry directly.
    /// </summary>
    public async Task WriteAuditAsync(string docId, string action, string? beforeJson, string? afterJson, string actor, CancellationToken ct = default)
    {
        var entry = new DocAuditEntry(
            Ulid.NewUlid().ToString(),
            docId,
            actor,
            action,
            beforeJson,
            afterJson,
            DateTimeOffset.UtcNow);

        // Use shard index for audit (always available)
        await _docsShardIndex.WriteAuditAsync(entry);
    }
}