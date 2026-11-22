using Eling.Backend.Dtos;
using Eling.Core;
using Eling.Core.Docs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Eling.Backend.Endpoints;

public static class DocEndpoints
{
    public static WebApplication MapDocRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/docs").MapDocEndpoints();
        return app;
    }

    private static RouteGroupBuilder MapDocEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/docs?kind=spec&topic=...&ownership=managed&origin=current&limit=50&offset=0
        group.MapGet("/", ListDocsAsync);

        // GET /api/docs/search?q=...&kind=spec&topic=...&ownership=managed&origin=current&limit=20
        group.MapGet("/search", SearchDocsAsync);

        // GET /api/docs/{id}
        group.MapGet("/{id}", GetDocAsync);

        // GET /api/docs/{id}/sections
        group.MapGet("/{id}/sections", GetDocSectionsAsync);

        // GET /api/docs/{id}/refs
        group.MapGet("/{id}/refs", GetDocRefsAsync);

        // GET /api/docs/{id}/relations
        group.MapGet("/{id}/relations", GetDocRelationsAsync);

        // POST /api/docs
        group.MapPost("/", CreateDocAsync);

        // PUT /api/docs/{id}
        group.MapPut("/{id}", UpdateDocAsync);

        // DELETE /api/docs/{id}
        group.MapDelete("/{id}", DeleteDocAsync);

        // POST /api/docs/{id}/annotate
        group.MapPost("/{id}/annotate", AnnotateSectionAsync);

        // POST /api/docs/{id}/promote
        group.MapPost("/{id}/promote", PromoteDocAsync);

        // POST /api/docs/{id}/relation
        group.MapPost("/{id}/relation", AddRelationAsync);

        // POST /api/docs/scan
        group.MapPost("/scan", ScanAsync);

        // GET /api/docs/staleness
        group.MapGet("/staleness", GetStalenessAsync);

        // GET /api/docs/audit?docId=...&limit=100
        group.MapGet("/audit", GetAuditAsync);

        // GET /api/docs/offer
        group.MapGet("/offer", GetOfferAsync);

        // POST /api/docs/offer
        group.MapPost("/offer", AnswerOfferAsync);

        // GET /api/docs/policy
        group.MapGet("/policy", GetPolicyAsync);

        // PUT /api/docs/policy
        group.MapPut("/policy", SetPolicyAsync);

        return group;
    }

    // ---- List ----

    private static async Task<Results<Ok<DocListDto>, BadRequest<string>>> ListDocsAsync(
        DocsService service,
        string? kind,
        string? topic,
        string? ownership,
        string? origin,
        int? limit,
        int? offset)
    {
        var all = await service.ListAsync(kind, topic, ownership, origin);

        // Sort by order then title
        all = all.OrderBy(d => d.Order).ThenBy(d => d.Title).ToList();

        if (limit is < 1 or > 200)
            return TypedResults.BadRequest("Query parameter 'limit' must be between 1 and 200.");

        if (offset is < 0)
            return TypedResults.BadRequest("Query parameter 'offset' must be zero or greater.");

        if (offset is > 0)
            all = all.Skip(offset.Value).ToList();

        if (limit is not null)
            all = all.Take(limit.Value).ToList();

        // Group by topic
        var topics = all
            .GroupBy(d => d.Topic)
            .Select(g => new DocTopicGroupDto(g.Key, g.Select(DocListItemDto.From).ToList().AsReadOnly()))
            .ToList()
            .AsReadOnly();

        return TypedResults.Ok(new DocListDto(topics));
    }

    // ---- Search ----

    private static async Task<Results<Ok<IReadOnlyCollection<DocSearchResultDto>>, BadRequest<string>>> SearchDocsAsync(
        DocsService service,
        string q,
        string? kind,
        string? topic,
        string? ownership,
        string? origin,
        int? limit)
    {
        if (string.IsNullOrWhiteSpace(q))
            return TypedResults.BadRequest("Query parameter 'q' is required.");

        var results = await service.SearchAsync(q, kind, topic, ownership, origin);
        var count = limit ?? 20;

        var limited = results.Take(count).Select(DocSearchResultDto.From).ToList().AsReadOnly();
        return TypedResults.Ok((IReadOnlyCollection<DocSearchResultDto>)limited);
    }

    // ---- Get ----

    private static async Task<Results<Ok<DocReadResultDto>, NotFound, BadRequest<string>>> GetDocAsync(
        DocsService service,
        string id)
    {
        if (!TryParseDocId(id, out var docId))
            return TypedResults.BadRequest("Invalid document ID format.");

        var result = await service.ReadAsync(docId, includeSections: true, includeRefs: true, includeContent: true);
        return result is not null ? TypedResults.Ok(DocReadResultDto.From(result)) : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<IReadOnlyCollection<DocSectionDto>>, NotFound, BadRequest<string>>> GetDocSectionsAsync(
        DocsService service,
        string id)
    {
        if (!TryParseDocId(id, out var docId))
            return TypedResults.BadRequest("Invalid document ID format.");

        var sections = await service.GetSectionsAsync(docId);
        if (sections is null) return TypedResults.NotFound();
        var list = sections.Select(DocSectionDto.From).ToList();
        return TypedResults.Ok<IReadOnlyCollection<DocSectionDto>>(list);
    }

    private static async Task<Results<Ok<IReadOnlyCollection<DocRefDto>>, NotFound, BadRequest<string>>> GetDocRefsAsync(
        DocsService service,
        string id)
    {
        if (!TryParseDocId(id, out var docId))
            return TypedResults.BadRequest("Invalid document ID format.");

        var refs = await service.GetRefsAsync(docId);
        if (refs is null) return TypedResults.NotFound();
        var list = refs.Select(DocRefDto.From).ToList();
        return TypedResults.Ok<IReadOnlyCollection<DocRefDto>>(list);
    }

    private static async Task<Results<Ok<IReadOnlyCollection<DocRelationDto>>, NotFound, BadRequest<string>>> GetDocRelationsAsync(
        DocsService service,
        string id)
    {
        if (!TryParseDocId(id, out var docId))
            return TypedResults.BadRequest("Invalid document ID format.");

        var relations = await service.GetRelationsAsync(docId);
        if (relations is null) return TypedResults.NotFound();
        var list = relations.Select(DocRelationDto.From).ToList();
        return TypedResults.Ok<IReadOnlyCollection<DocRelationDto>>(list);
    }

    // ---- Create ----

    private static async Task<Results<Created<DocSaveResponse>, BadRequest<string>>> CreateDocAsync(
        DocsService service,
        MemoryChangeBroadcaster broadcaster,
        DocSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RelPath))
            return TypedResults.BadRequest("RelPath is required.");

        if (string.IsNullOrWhiteSpace(request.Content))
            return TypedResults.BadRequest("Content is required.");

        DocKind kind;
        if (string.IsNullOrEmpty(request.Kind) || !Enum.TryParse<DocKind>(request.Kind, ignoreCase: true, out kind))
        {
            kind = DocKind.Doc; // Default
        }

        var doc = await service.CreateDocumentAsync(
            request.RelPath,
            request.Content,
            kind,
            request.Topic,
            request.Status is not null ? new DocStatusValue(kind, int.Parse(request.Status)) : null,
            request.Order ?? 0);

        broadcaster.Notify(DocsChangeTopics.Docs);
        return TypedResults.Created($"/api/docs/{doc.Id.Value}", DocSaveResponse.From(doc, "created"));
    }

    // ---- Update ----

    private static async Task<Results<Ok<DocSaveResponse>, NotFound, BadRequest<string>>> UpdateDocAsync(
        DocsService service,
        MemoryChangeBroadcaster broadcaster,
        string id,
        DocUpdateRequest request)
    {
        if (!TryParseDocId(id, out var docId))
            return TypedResults.BadRequest("Invalid document ID format.");

        if (string.IsNullOrWhiteSpace(request.Content))
            return TypedResults.BadRequest("Content is required.");

        var doc = await service.UpdateDocumentAsync(docId.Value, request.Content);
        if (doc is null) return TypedResults.NotFound();

        broadcaster.Notify(DocsChangeTopics.Docs);
        return TypedResults.Ok(DocSaveResponse.From(doc, "updated"));
    }

    // ---- Delete (Archive) ----

    private static async Task<Results<NoContent, NotFound, BadRequest<string>>> DeleteDocAsync(
        DocsService service,
        MemoryChangeBroadcaster broadcaster,
        string id)
    {
        if (!TryParseDocId(id, out var docId))
            return TypedResults.BadRequest("Invalid document ID format.");

        var deleted = await service.ArchiveDocumentAsync(docId.Value);
        if (deleted)
        {
            broadcaster.Notify(DocsChangeTopics.Docs);
            return TypedResults.NoContent();
        }
        return TypedResults.NotFound();
    }

    // ---- Annotate Section ----

    private static async Task<Results<Ok<DocAnnotateResponse>, NotFound, BadRequest<string>>> AnnotateSectionAsync(
        DocsService service,
        MemoryChangeBroadcaster broadcaster,
        string id,
        DocAnnotateRequest request)
    {
        if (!TryParseDocId(id, out var docId))
            return TypedResults.BadRequest("Invalid document ID format.");

        if (string.IsNullOrWhiteSpace(request.Anchor))
            return TypedResults.BadRequest("Anchor is required.");

        var success = await service.AnnotateSectionAsync(
            docId.Value,
            request.Anchor,
            request.Status,
            request.Annotation);

        if (!success) return TypedResults.NotFound();

        broadcaster.Notify(DocsChangeTopics.Docs);
        return TypedResults.Ok(new DocAnnotateResponse(true));
    }

    // ---- Promote ----

    private static async Task<Results<Ok<DocPromoteResponse>, NotFound, BadRequest<string>>> PromoteDocAsync(
        DocsService service,
        MemoryChangeBroadcaster broadcaster,
        string id)
    {
        if (!TryParseDocId(id, out var docId))
            return TypedResults.BadRequest("Invalid document ID format.");

        var doc = await service.PromoteDocumentAsync(docId.Value);
        if (doc is null) return TypedResults.NotFound();

        broadcaster.Notify(DocsChangeTopics.Docs);
        return TypedResults.Ok(DocPromoteResponse.From(doc));
    }

    // ---- Add Relation ----

    private static async Task<Results<Ok<DocRelationResponse>, NotFound, BadRequest<string>>> AddRelationAsync(
        DocsService service,
        MemoryChangeBroadcaster broadcaster,
        string id,
        DocRelationRequest request)
    {
        if (!TryParseDocId(id, out var sourceId))
            return TypedResults.BadRequest("Invalid source document ID format.");

        if (!TryParseDocId(request.TargetId, out var targetId))
            return TypedResults.BadRequest("Invalid target document ID format.");

        RelationKind kind;
        if (!Enum.TryParse<RelationKind>(request.Kind, ignoreCase: true, out kind))
            return TypedResults.BadRequest($"Invalid relation kind '{request.Kind}'. Valid: Implements, Supersedes, References");

        try
        {
            await service.AddRelationAsync(sourceId.Value, targetId.Value, kind);
            broadcaster.Notify(DocsChangeTopics.Docs);
            return TypedResults.Ok(new DocRelationResponse(true));
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    // ---- Scan ----

    private static async Task<Ok<DocIndexResponse>> ScanAsync(
        DocsService service,
        MemoryChangeBroadcaster broadcaster,
        DocScanRequest? request = null)
    {
        var full = request?.Full ?? false;
        var paths = request?.Paths;
        var result = await service.ScanAsync(full, paths);
        broadcaster.Notify(DocsChangeTopics.Docs);
        return TypedResults.Ok(DocIndexResponse.From(result));
    }

    // ---- Staleness ----

    private static async Task<Ok<StalenessReportDto>> GetStalenessAsync(DocsService service)
    {
        var report = await service.GetStalenessReportAsync();
        return TypedResults.Ok(StalenessReportDto.From(report));
    }

    // ---- Audit ----

    private static async Task<Ok<IReadOnlyCollection<DocAuditEntryDto>>> GetAuditAsync(
        DocsService service,
        string? docId,
        int? limit)
    {
        var entries = await service.GetAuditAsync(docId, limit ?? 100);
        var list = entries.Select(DocAuditEntryDto.From).ToList();
        return TypedResults.Ok<IReadOnlyCollection<DocAuditEntryDto>>(list);
    }

    // ---- Offer ----

    private static async Task<Ok<OfferCheckResultDto>> GetOfferAsync(DocsService service)
    {
        var result = await service.CheckOfferPolicyAsync();
        return TypedResults.Ok(OfferCheckResultDto.From(result));
    }

    private static async Task<Ok<OfferResponse>> AnswerOfferAsync(
        DocsService service,
        DocOfferRequest request)
    {
        var state = Enum.TryParse<OfferState>(request.Answer, ignoreCase: true, out var parsed)
            ? parsed
            : OfferState.Accepted;
        await service.AnswerOfferAsync(state);
        return TypedResults.Ok(OfferResponse.FromAnswer(state));
    }

    // ---- Policy ----

    private static async Task<Ok<ScopePolicyResponse>> GetPolicyAsync(DocsService service)
    {
        var coverage = await service.GetCoveragePolicyAsync();
        var decision = await service.GetScopeDecisionAsync();
        var coverageDto = new CoveragePolicyDto(
            coverage.Include.ToArray(),
            coverage.Exclude.ToArray());
        return TypedResults.Ok(new ScopePolicyResponse(coverageDto, decision.ToString().ToLowerInvariant()));
    }

    private static async Task<Ok<ScopePolicyResponse>> SetPolicyAsync(
        DocsService service,
        DocPolicyRequest request)
    {
        var policy = new CoveragePolicy
        {
            Include = request.Coverage?.Include ?? new[] { "docs/**", ".eling/docs/**" },
            Exclude = request.Coverage?.Exclude ?? new[] { ".superpowers/sdd/**" }
        };

        ScopeDecision? decision = null;
        if (!string.IsNullOrEmpty(request.Decision))
        {
            decision = Enum.TryParse<ScopeDecision>(request.Decision, ignoreCase: true, out var parsed)
                ? parsed
                : ScopeDecision.Ask;
        }

        await service.SetCoveragePolicyAsync(policy, decision);

        var coverageDto = new CoveragePolicyDto(
            policy.Include.ToArray(),
            policy.Exclude.ToArray());
        return TypedResults.Ok(new ScopePolicyResponse(coverageDto, decision?.ToString().ToLowerInvariant() ?? "ask"));
    }

    private static bool TryParseDocId(string id, out DocId docId)
    {
        try
        {
            docId = DocId.Parse(id);
            return true;
        }
        catch (ArgumentException)
        {
            docId = default;
            return false;
        }
    }
}