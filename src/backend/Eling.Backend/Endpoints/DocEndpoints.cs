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
        DocsService service,
        string id,
        bool includeSections = true,
        bool includeRefs = true,
        bool includeContent = true)
    {
        var result = await service.ReadAsync(DocId.Parse(id), includeSections, includeRefs, includeContent);
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

    private static async Task<IResult> CreateAsync(DocsService service, DocSaveRequest request, string? workspace = null)
    {
        var root = workspace ?? service.WorkspaceRoots.FirstOrDefault()
                   ?? throw new InvalidOperationException("No workspace is registered.");
        var kind = ParseKind(request.Kind) ?? DocKind.Doc;
        var status = int.TryParse(request.Status, out var parsed) ? parsed : 0;
        var doc = await service.CreateDocumentAsync(root, request.RelPath, request.Content, kind, request.Topic, status, request.Order ?? 0);
        return Results.Ok(DocDto.From(doc));
    }

    private static async Task<IResult> UpdateAsync(DocsService service, string id, DocUpdateRequest request)
    {
        var doc = await service.UpdateDocumentAsync(DocId.Parse(id), request.Content);
        return doc is null ? Results.NotFound() : Results.Ok(DocDto.From(doc));
    }

    private static async Task<IResult> ArchiveAsync(DocsService service, string id)
        => Results.Ok(await service.ArchiveDocumentAsync(DocId.Parse(id)));

    private static async Task<IResult> AnnotateAsync(DocsService service, string id, DocAnnotateRequest request)
        => Results.Ok(await service.AnnotateSectionAsync(DocId.Parse(id), request.Anchor, request.Status, request.Annotation));

    private static async Task<IResult> ReindexAsync(DocsService service, DocScanRequest? request = null, string? workspace = null)
        => Results.Ok(DocIndexResponse.From(
            await service.ScanAsync(request?.Full ?? false, request?.Paths, workspace)));

    private static DocKind? ParseKind(string? value)
        => string.IsNullOrWhiteSpace(value) ? null
            : Enum.TryParse<DocKind>(value, true, out var kind) ? kind : null;
}
