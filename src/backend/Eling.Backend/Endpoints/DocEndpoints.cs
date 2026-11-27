using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Core.Docs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Eling.Backend.Endpoints;

/// <summary>REST surface for the unified docs catalog.</summary>
public static class DocEndpoints
{
    public static WebApplication MapDocRoutes(this WebApplication app)
    {
        app.MapGroup("/api/docs").MapDocEndpoints();
        return app;
    }

    private static RouteGroupBuilder MapDocEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync);
        group.MapGet("/search", SearchAsync);
        group.MapGet("/doc", ReadAsync);
        group.MapGet("/{id}", ReadByIdAsync);
        group.MapGet("/staleness", StalenessAsync);
        group.MapGet("/audit", AuditAsync);
        group.MapGet("/policy", PolicyAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", ArchiveAsync);
        group.MapPost("/{id}/annotate", AnnotateAsync);
        group.MapPost("/reindex", ReindexAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(
        DocsService service,
        string[]? workspace = null,
        string? kind = null,
        string? topic = null,
        string? sort = null,
        string? order = null,
        int limit = 50,
        int offset = 0)
    {
        var page = await service.ListPageAsync(workspace, ParseKind(kind), topic, sort ?? "name", order ?? "asc", limit, offset);
        var groups = page.Items.Select(DocListItemDto.From)
            .GroupBy(item => item.Topic)
            .Select(group => new DocTopicGroupDto(group.Key, group.ToList().AsReadOnly()))
            .ToList().AsReadOnly();
        return Results.Ok(new DocListDto(groups, page.Total));
    }

    private static async Task<IResult> SearchAsync(
        DocsService service,
        string q,
        string? workspace = null,
        string? kind = null,
        string? topic = null,
        int limit = 50)
    {
        var results = await service.SearchAsync(q, workspace, ParseKind(kind), topic, limit);
        return Results.Ok(results.Select(DocSearchResultDto.From).ToList().AsReadOnly());
    }

    private static async Task<IResult> ReadAsync(
        HttpContext httpContext,
        DocsService service,
        string id,
        bool includeSections = true,
        bool includeRefs = true,
        bool includeContent = true)
    {
        if (!DocId.TryParse(id, out var parsed)) return InvalidId(httpContext, id);
        var result = await service.ReadAsync(parsed, includeSections, includeRefs, includeContent);
        return result is null ? Results.NotFound() : Results.Ok(DocReadResultDto.From(result));
    }

    private static async Task<IResult> ReadByIdAsync(
        HttpContext httpContext,
        DocsService service,
        string id,
        bool includeSections = true,
        bool includeRefs = true,
        bool includeContent = true)
    {
        if (!DocId.TryParse(id, out var parsed)) return InvalidId(httpContext, id);
        var result = await service.ReadAsync(parsed, includeSections, includeRefs, includeContent);
        return result is null ? Results.NotFound() : Results.Ok(DocReadResultDto.From(result));
    }

    private static async Task<IResult> StalenessAsync(DocsService service)
    {
        var report = await service.GetStalenessReportAsync();
        return Results.Ok(StalenessReportDto.From(report));
    }

    private static async Task<IResult> AuditAsync(DocsService service, string? docId = null, int limit = 100)
        => Results.Ok((await service.GetAuditAsync(docId, limit)).Select(DocAuditEntryDto.From).ToList().AsReadOnly());

    private static async Task<IResult> PolicyAsync(DocsService service, string? workspace = null)
        => Results.Ok(await service.GetCoveragePolicyAsync(workspace));

    private static async Task<IResult> CreateAsync(HttpContext httpContext, DocsService service, DocSaveRequest request, string? workspace = null)
    {
        var root = workspace ?? service.WorkspaceRoots.FirstOrDefault();
        if (root is null)
        {
            httpContext.Items[ApiEnvelopeMiddleware.ErrorCodeItem] = "no_workspace_registered";
            return Results.BadRequest("No workspace is registered - add one from the dashboard workspaces page and try again.");
        }
        var kind = ParseKind(request.Kind) ?? DocKind.Doc;
        var status = int.TryParse(request.Status, out var parsed) ? parsed : 0;
        var doc = await service.CreateDocumentAsync(root, request.RelPath, request.Content, kind, request.Topic, status, request.Order ?? 0);
        return Results.Ok(DocDto.From(doc));
    }

    private static async Task<IResult> UpdateAsync(HttpContext httpContext, DocsService service, string id, DocUpdateRequest request)
    {
        if (!DocId.TryParse(id, out var parsed)) return InvalidId(httpContext, id);
        var doc = await service.UpdateDocumentAsync(parsed, request.Content);
        return doc is null ? Results.NotFound() : Results.Ok(DocDto.From(doc));
    }

    private static async Task<IResult> ArchiveAsync(HttpContext httpContext, DocsService service, string id)
    {
        if (!DocId.TryParse(id, out var parsed)) return InvalidId(httpContext, id);
        return Results.Ok(await service.ArchiveDocumentAsync(parsed));
    }

    private static async Task<IResult> AnnotateAsync(HttpContext httpContext, DocsService service, string id, DocAnnotateRequest request)
    {
        if (!DocId.TryParse(id, out var parsed)) return InvalidId(httpContext, id);
        return Results.Ok(await service.AnnotateSectionAsync(parsed, request.Anchor, request.Status, request.Annotation));
    }

    private static async Task<IResult> ReindexAsync(DocsService service, DocScanRequest? request = null, string? workspace = null)
        => Results.Ok(DocIndexResponse.From(
            await service.ScanAsync(request?.Full ?? false, request?.Paths, workspace)));

    private static IResult InvalidId(HttpContext httpContext, string id)
    {
        httpContext.Items[ApiEnvelopeMiddleware.ErrorCodeItem] = "invalid_doc_id";
        return Results.BadRequest(
            $"The document id '{id}' is not a valid identifier - check the link or the id and try again.");
    }

    private static DocKind? ParseKind(string? value)
        => string.IsNullOrWhiteSpace(value) ? null
            : Enum.TryParse<DocKind>(value, true, out var kind) ? kind : null;
}
