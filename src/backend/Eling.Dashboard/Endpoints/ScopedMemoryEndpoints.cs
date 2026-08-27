using Eling.Application;
using Eling.Core;
using Eling.Dashboard.Dtos;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Eling.Dashboard.Endpoints;

public static class ScopedMemoryEndpoints
{
    public static WebApplication MapScopedMemoryRoutes(this WebApplication app)
    {
        var global = app.MapGroup("/api/global/memories");
        global.MapGet("/", ListGlobalAsync);
        global.MapGet("/search", SearchGlobalAsync);
        global.MapGet("/{id}", GetGlobalAsync);
        global.MapPost("/", CreateGlobalAsync);
        global.MapDelete("/{id}", DeleteGlobalAsync);
        global.MapPatch("/{id}", UpdateGlobalAsync);
        global.MapPost("/rebuild-index", RebuildGlobalAsync);

        var aggregated = app.MapGroup("/api/aggregated");
        aggregated.MapGet("/memories", ListAggregatedAsync);
        aggregated.MapGet("/memories/search", SearchAggregatedAsync);

        var project = app.MapGroup("/api/project");
        project.MapGet("/memories", ListProjectAsync);
        project.MapGet("/memories/search", SearchProjectAsync);
        project.MapGet("/memories/{id}", GetProjectAsync);
        project.MapPost("/memories", CreateProjectAsync);
        project.MapDelete("/memories/{id}", DeleteProjectAsync);
        project.MapPatch("/memories/{id}", UpdateProjectAsync);

        var copy = app.MapGroup("/api/scoped");
        copy.MapPost("/copy-to-project", CopyToProjectAsync);
        copy.MapPost("/promote-to-global", PromoteToGlobalAsync);

        return app;
    }

    // ---- Global ----

    private static async Task<Results<Ok<IReadOnlyCollection<ScopedMemoryDto>>, BadRequest<string>>> ListGlobalAsync(
        RuntimeRegistry registry, string? status, string? type, int? limit, int? offset)
    {
        var service = registry.GetGlobalMemoryService();
        var all = await service.ListAllAsync();
        all = all.OrderByDescending(m => m.CreatedAt).ToList();
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<MemoryStatus>(status, true, out var parsed)) return TypedResults.BadRequest($"Invalid status '{status}'");
            all = all.Where(m => m.Status == parsed).ToList();
        }
        if (!string.IsNullOrEmpty(type))
        {
            if (!Enum.TryParse<MemoryType>(type, true, out var parsed)) return TypedResults.BadRequest($"Invalid type '{type}'");
            all = all.Where(m => m.Type == parsed).ToList();
        }
        if (offset is > 0) all = all.Skip(offset.Value).ToList();
        if (limit is not null) all = all.Take(limit.Value).ToList();
        var dtos = all.Select(m => ScopedMemoryDto.From(m, MemoryScopeKind.Global, null)).ToList().AsReadOnly();
        return TypedResults.Ok((IReadOnlyCollection<ScopedMemoryDto>)dtos);
    }

    private static async Task<Results<Ok<IReadOnlyCollection<ScopedSearchResultDto>>, BadRequest<string>>> SearchGlobalAsync(
        RuntimeRegistry registry, string q, int? limit)
    {
        if (string.IsNullOrWhiteSpace(q)) return TypedResults.BadRequest("Query parameter 'q' is required.");
        var service = registry.GetGlobalMemoryService();
        var results = await service.SearchAsync(q);
        var list = results.Select(r => new ScopedSearchResultDto(r.Id.Value, r.Rank, "global", null)).ToList();
        if (limit is not null) list = list.Take(limit.Value).ToList();
        return TypedResults.Ok((IReadOnlyCollection<ScopedSearchResultDto>)list.AsReadOnly());
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound>> GetGlobalAsync(RuntimeRegistry registry, string id)
    {
        if (!TryParseMemoryId(id, out var memoryId)) return TypedResults.NotFound();
        var service = registry.GetGlobalMemoryService();
        var memory = await service.GetByIdAsync(memoryId);
        return memory is null ? TypedResults.NotFound() : TypedResults.Ok(ScopedMemoryDto.From(memory, MemoryScopeKind.Global, null));
    }

    private static async Task<Results<Created<ScopedMemoryDto>, BadRequest<string>>> CreateGlobalAsync(RuntimeRegistry registry, SaveMemoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content)) return TypedResults.BadRequest("Content is required.");
        MemoryType type = MemoryType.Note;
        if (!string.IsNullOrEmpty(request.Type) && !Enum.TryParse<MemoryType>(request.Type, true, out type))
            return TypedResults.BadRequest($"Invalid type '{request.Type}'");
        var memory = new Memory(type, request.Content, request.Tags, request.Source);
        var service = registry.GetGlobalMemoryService();
        var saved = await service.SaveAsync(memory);
        var dto = ScopedMemoryDto.From(saved, MemoryScopeKind.Global, null);
        return TypedResults.Created($"/api/global/memories/{saved.Id}", dto);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteGlobalAsync(RuntimeRegistry registry, string id)
    {
        if (!TryParseMemoryId(id, out var memoryId)) return TypedResults.NotFound();
        var service = registry.GetGlobalMemoryService();
        return await service.DeleteAsync(memoryId) ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> UpdateGlobalAsync(RuntimeRegistry registry, string id, UpdateMemoryRequest request)
    {
        if (!TryParseMemoryId(id, out var memoryId)) return TypedResults.NotFound();
        MemoryType? type = null;
        if (!string.IsNullOrEmpty(request.Type))
        {
            if (!Enum.TryParse<MemoryType>(request.Type, true, out var parsed)) return TypedResults.BadRequest($"Invalid type '{request.Type}'");
            type = parsed;
        }
        MemoryStatus? status = null;
        if (!string.IsNullOrEmpty(request.Status))
        {
            if (!Enum.TryParse<MemoryStatus>(request.Status, true, out var parsed)) return TypedResults.BadRequest($"Invalid status '{request.Status}'");
            status = parsed;
        }
        var service = registry.GetGlobalMemoryService();
        var updated = await service.UpdateAsync(memoryId, request.Content, type, request.Tags?.ToArray(), request.Source, status);
        return updated is null ? TypedResults.NotFound() : TypedResults.Ok(ScopedMemoryDto.From(updated, MemoryScopeKind.Global, null));
    }

    private static async Task<NoContent> RebuildGlobalAsync(RuntimeRegistry registry)
    {
        await registry.GetGlobalMemoryService().RebuildIndexAsync();
        return TypedResults.NoContent();
    }

    // ---- Aggregated ----

    private static async Task<Ok<IReadOnlyCollection<ScopedMemoryDto>>> ListAggregatedAsync(RuntimeRegistry registry, string? status, string? type, int? limit, int? offset)
    {
        MemoryStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<MemoryStatus>(status, true, out var parsedStatus)) statusFilter = parsedStatus;
        var all = await registry.ListAggregatedAsync(statusFilter);
        var filtered = all.AsEnumerable();
        if (!string.IsNullOrEmpty(type) && Enum.TryParse<MemoryType>(type, true, out var typeParsed))
            filtered = filtered.Where(s => s.Memory.Type == typeParsed);
        filtered = filtered.OrderByDescending(s => s.Memory.CreatedAt);
        if (offset is > 0) filtered = filtered.Skip(offset.Value);
        if (limit is not null) filtered = filtered.Take(limit.Value);
        var dtos = filtered.Select(s => ScopedMemoryDto.From(s.Memory, s.Scope, s.ProjectRoot)).ToList().AsReadOnly();
        return TypedResults.Ok((IReadOnlyCollection<ScopedMemoryDto>)dtos);
    }

    private static async Task<Ok<IReadOnlyCollection<ScopedSearchResultDto>>> SearchAggregatedAsync(RuntimeRegistry registry, string q, int? limit)
    {
        if (string.IsNullOrWhiteSpace(q)) return TypedResults.Ok((IReadOnlyCollection<ScopedSearchResultDto>)Array.Empty<ScopedSearchResultDto>());
        var results = await registry.SearchAggregatedAsync(q, limit);
        var dtos = results.Select(r => new ScopedSearchResultDto(r.Id.Value, r.Rank, r.Scope == MemoryScopeKind.Global ? "global" : "project", r.ProjectRoot)).ToList().AsReadOnly();
        return TypedResults.Ok((IReadOnlyCollection<ScopedSearchResultDto>)dtos);
    }

    // ---- Project ----

    private static async Task<Results<Ok<IReadOnlyCollection<ScopedMemoryDto>>, BadRequest<string>, NotFound<string>>> ListProjectAsync(
        RuntimeRegistry registry, string projectRoot, string? status, string? type, int? limit, int? offset)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        var service = registry.TryResolveMemoryServiceByProjectRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found or not alive");
        var all = await service.ListAllAsync();
        all = all.OrderByDescending(m => m.CreatedAt).ToList();
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<MemoryStatus>(status, true, out var parsed)) return TypedResults.BadRequest($"Invalid status '{status}'");
            all = all.Where(m => m.Status == parsed).ToList();
        }
        if (!string.IsNullOrEmpty(type))
        {
            if (!Enum.TryParse<MemoryType>(type, true, out var parsed)) return TypedResults.BadRequest($"Invalid type '{type}'");
            all = all.Where(m => m.Type == parsed).ToList();
        }
        if (offset is > 0) all = all.Skip(offset.Value).ToList();
        if (limit is not null) all = all.Take(limit.Value).ToList();
        var dtos = all.Select(m => ScopedMemoryDto.From(m, MemoryScopeKind.Project, projectRoot)).ToList().AsReadOnly();
        return TypedResults.Ok((IReadOnlyCollection<ScopedMemoryDto>)dtos);
    }

    private static async Task<Results<Ok<IReadOnlyCollection<ScopedSearchResultDto>>, BadRequest<string>, NotFound<string>>> SearchProjectAsync(
        RuntimeRegistry registry, string projectRoot, string q, int? limit)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (string.IsNullOrWhiteSpace(q)) return TypedResults.BadRequest("q is required");
        var service = registry.TryResolveMemoryServiceByProjectRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found or not alive");
        var results = await service.SearchAsync(q);
        var list = results.Select(r => new ScopedSearchResultDto(r.Id.Value, r.Rank, "project", projectRoot)).ToList();
        if (limit is not null) list = list.Take(limit.Value).ToList();
        return TypedResults.Ok((IReadOnlyCollection<ScopedSearchResultDto>)list.AsReadOnly());
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>, NotFound<string>>> GetProjectAsync(RuntimeRegistry registry, string id, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (!TryParseMemoryId(id, out var memoryId)) return TypedResults.NotFound();
        var service = registry.TryResolveMemoryServiceByProjectRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found");
        var memory = await service.GetByIdAsync(memoryId);
        return memory is null ? TypedResults.NotFound() : TypedResults.Ok(ScopedMemoryDto.From(memory, MemoryScopeKind.Project, projectRoot));
    }

    private static async Task<Results<Created<ScopedMemoryDto>, BadRequest<string>, NotFound<string>>> CreateProjectAsync(RuntimeRegistry registry, string projectRoot, SaveMemoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (string.IsNullOrWhiteSpace(request.Content)) return TypedResults.BadRequest("Content is required.");
        var service = registry.TryResolveMemoryServiceByProjectRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found or not alive");
        MemoryType type = MemoryType.Note;
        if (!string.IsNullOrEmpty(request.Type) && !Enum.TryParse<MemoryType>(request.Type, true, out type))
            return TypedResults.BadRequest($"Invalid type '{request.Type}'");
        var memory = new Memory(type, request.Content, request.Tags, request.Source);
        var saved = await service.SaveAsync(memory);
        var dto = ScopedMemoryDto.From(saved, MemoryScopeKind.Project, projectRoot);
        return TypedResults.Created($"/api/project/memories/{saved.Id}?projectRoot={Uri.EscapeDataString(projectRoot)}", dto);
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<string>, NotFound<string>>> DeleteProjectAsync(RuntimeRegistry registry, string id, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (!TryParseMemoryId(id, out var memoryId)) return TypedResults.NotFound();
        var service = registry.TryResolveMemoryServiceByProjectRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found");
        return await service.DeleteAsync(memoryId) ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>, NotFound<string>>> UpdateProjectAsync(RuntimeRegistry registry, string id, string projectRoot, UpdateMemoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (!TryParseMemoryId(id, out var memoryId)) return TypedResults.NotFound();
        var service = registry.TryResolveMemoryServiceByProjectRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found");
        MemoryType? type = null;
        if (!string.IsNullOrEmpty(request.Type))
        {
            if (!Enum.TryParse<MemoryType>(request.Type, true, out var parsed)) return TypedResults.BadRequest($"Invalid type '{request.Type}'");
            type = parsed;
        }
        MemoryStatus? status = null;
        if (!string.IsNullOrEmpty(request.Status))
        {
            if (!Enum.TryParse<MemoryStatus>(request.Status, true, out var parsed)) return TypedResults.BadRequest($"Invalid status '{request.Status}'");
            status = parsed;
        }
        var updated = await service.UpdateAsync(memoryId, request.Content, type, request.Tags?.ToArray(), request.Source, status);
        return updated is null ? TypedResults.NotFound() : TypedResults.Ok(ScopedMemoryDto.From(updated, MemoryScopeKind.Project, projectRoot));
    }

    // ---- Copy / Promote ----

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> CopyToProjectAsync(RuntimeRegistry registry, CopyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id)) return TypedResults.BadRequest("Id is required");
        if (string.IsNullOrWhiteSpace(request.TargetProjectRoot)) return TypedResults.BadRequest("TargetProjectRoot is required");
        if (!TryParseMemoryId(request.Id, out var memoryId)) return TypedResults.NotFound();

        Memory? sourceMemory = null;
        if (request.SourceScope == "global")
        {
            sourceMemory = await registry.GetGlobalMemoryService().GetByIdAsync(memoryId);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.SourceProjectRoot)) return TypedResults.BadRequest("SourceProjectRoot required for project source");
            var srcService = registry.TryResolveMemoryServiceByProjectRoot(request.SourceProjectRoot);
            if (srcService is null) return TypedResults.NotFound();
            sourceMemory = await srcService.GetByIdAsync(memoryId);
        }
        if (sourceMemory is null) return TypedResults.NotFound();

        var targetService = registry.TryResolveMemoryServiceByProjectRoot(request.TargetProjectRoot);
        if (targetService is null) return TypedResults.BadRequest($"Target project '{request.TargetProjectRoot}' not alive");

        var copy = new Memory(sourceMemory.Type, sourceMemory.Content, sourceMemory.Tags, sourceMemory.Source, sourceMemory.Status);
        var saved = await targetService.SaveAsync(copy);
        return TypedResults.Ok(ScopedMemoryDto.From(saved, MemoryScopeKind.Project, request.TargetProjectRoot));
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> PromoteToGlobalAsync(RuntimeRegistry registry, PromoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id)) return TypedResults.BadRequest("Id is required");
        if (string.IsNullOrWhiteSpace(request.SourceProjectRoot)) return TypedResults.BadRequest("SourceProjectRoot is required");
        if (!TryParseMemoryId(request.Id, out var memoryId)) return TypedResults.NotFound();
        var srcService = registry.TryResolveMemoryServiceByProjectRoot(request.SourceProjectRoot);
        if (srcService is null) return TypedResults.NotFound();
        var sourceMemory = await srcService.GetByIdAsync(memoryId);
        if (sourceMemory is null) return TypedResults.NotFound();
        var copy = new Memory(sourceMemory.Type, sourceMemory.Content, sourceMemory.Tags, sourceMemory.Source, sourceMemory.Status);
        var saved = await registry.GetGlobalMemoryService().SaveAsync(copy);
        return TypedResults.Ok(ScopedMemoryDto.From(saved, MemoryScopeKind.Global, null));
    }

    private static bool TryParseMemoryId(string id, out MemoryId memoryId)
    {
        try { memoryId = MemoryId.Parse(id); return true; } catch (ArgumentException) { memoryId = default; return false; }
    }
}
