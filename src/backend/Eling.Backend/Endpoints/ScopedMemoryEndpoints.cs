using Eling.Backend.Dtos;
using Eling.Core;
using Eling.Core.Memory;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Eling.Backend.Endpoints;

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
        copy.MapPost("/move-to-project", MoveToProjectAsync);
        copy.MapPost("/promote-to-global", PromoteToGlobalAsync);
        copy.MapPost("/move-to-global", MoveToGlobalAsync);
        copy.MapPost("/copy-to-local", CopyToLocalAsync);
        copy.MapPost("/move-to-local", MoveToLocalAsync);

        return app;
    }

    // ---- Global ----

    private static async Task<Results<Ok<IReadOnlyCollection<ScopedMemoryDto>>, BadRequest<string>>> ListGlobalAsync(
        RuntimeRegistry registry, string? status, string? type, int? limit, int? offset)
    {
        var service = registry.GetGlobalMemoryService();
        var all = await service.ListAllAsync();
        all = all.OrderByDescending(m => m.UpdatedAt).ThenByDescending(m => m.CreatedAt).ToList();
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
        var list = results.Select(ScopedSearchResultDto.Global).ToList();
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

    private static async Task<Results<Created<ScopedMemoryDto>, BadRequest<string>>> CreateGlobalAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, SaveMemoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content)) return TypedResults.BadRequest("Content is required.");
        MemoryType type = MemoryType.Note;
        if (!string.IsNullOrEmpty(request.Type) && !Enum.TryParse<MemoryType>(request.Type, true, out type))
            return TypedResults.BadRequest($"Invalid type '{request.Type}'");
        var memory = new Memory(type, request.Content, request.Tags, request.Source);
        var service = registry.GetGlobalMemoryService();
        var saved = await service.SaveAsync(memory);
        broadcaster.Notify("dashboard");
        var dto = ScopedMemoryDto.From(saved, MemoryScopeKind.Global, null);
        return TypedResults.Created($"/api/global/memories/{saved.Id}", dto);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteGlobalAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, string id)
    {
        if (!TryParseMemoryId(id, out var memoryId)) return TypedResults.NotFound();
        var service = registry.GetGlobalMemoryService();
        var deleted = await service.DeleteAsync(memoryId);
        if (deleted)
        {
            broadcaster.Notify("dashboard");
            return TypedResults.NoContent();
        }
        return TypedResults.NotFound();
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> UpdateGlobalAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, string id, UpdateMemoryRequest request)
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
        if (updated is null) return TypedResults.NotFound();
        broadcaster.Notify("dashboard");
        return TypedResults.Ok(ScopedMemoryDto.From(updated, MemoryScopeKind.Global, null));
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
        var dtos = results.Select(ScopedSearchResultDto.From).ToList().AsReadOnly();
        return TypedResults.Ok((IReadOnlyCollection<ScopedSearchResultDto>)dtos);
    }

    // ---- Project ----

    private static async Task<Results<Ok<IReadOnlyCollection<ScopedMemoryDto>>, BadRequest<string>, NotFound<string>>> ListProjectAsync(
        RuntimeRegistry registry, string projectRoot, string? status, string? type, int? limit, int? offset, string? tier = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (IsLocalTier(tier))
        {
            return await ListProjectLocalAsync(registry, projectRoot, status, type, limit, offset);
        }
        var service = registry.TryResolveMemoryServiceByScopeRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found or not alive");
        var all = await service.ListAllAsync();
        all = all.OrderByDescending(m => m.UpdatedAt).ThenByDescending(m => m.CreatedAt).ToList();
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

    private static bool IsLocalTier(string? tier)
        => string.Equals(tier?.Trim(), "project-local", StringComparison.OrdinalIgnoreCase);

    private static async Task<Results<Ok<IReadOnlyCollection<ScopedMemoryDto>>, BadRequest<string>, NotFound<string>>> ListProjectLocalAsync(
        RuntimeRegistry registry, string projectRoot, string? status, string? type, int? limit, int? offset)
    {
        var service = registry.TryResolveLocalServiceByScopeRoot(projectRoot, out var canonicalRoot);
        if (service is null) return TypedResults.Ok((IReadOnlyCollection<ScopedMemoryDto>)Array.Empty<ScopedMemoryDto>());
        var all = await service.ListAllAsync();
        all = all.OrderByDescending(m => m.UpdatedAt).ThenByDescending(m => m.CreatedAt).ToList();
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
        var dtos = all.Select(m => ScopedMemoryDto.From(m, MemoryScopeKind.ProjectLocal, canonicalRoot)).ToList().AsReadOnly();
        return TypedResults.Ok((IReadOnlyCollection<ScopedMemoryDto>)dtos);
    }

    private static async Task<Results<Ok<IReadOnlyCollection<ScopedSearchResultDto>>, BadRequest<string>, NotFound<string>>> SearchProjectAsync(
        RuntimeRegistry registry, string projectRoot, string q, int? limit, string? tier = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (string.IsNullOrWhiteSpace(q)) return TypedResults.BadRequest("q is required");
        if (IsLocalTier(tier))
        {
            var local = registry.TryResolveLocalServiceByScopeRoot(projectRoot, out var canonicalRoot);
            if (local is null) return TypedResults.Ok((IReadOnlyCollection<ScopedSearchResultDto>)Array.Empty<ScopedSearchResultDto>());
            var localResults = await local.SearchAsync(q);
            var localList = localResults.Select(r => ScopedSearchResultDto.ProjectLocal(r, canonicalRoot)).ToList();
            if (limit is not null) localList = localList.Take(limit.Value).ToList();
            return TypedResults.Ok((IReadOnlyCollection<ScopedSearchResultDto>)localList.AsReadOnly());
        }
        var service = registry.TryResolveMemoryServiceByScopeRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found or not alive");
        var results = await service.SearchAsync(q);
        var list = results.Select(r => ScopedSearchResultDto.Project(r, projectRoot)).ToList();
        if (limit is not null) list = list.Take(limit.Value).ToList();
        return TypedResults.Ok((IReadOnlyCollection<ScopedSearchResultDto>)list.AsReadOnly());
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>, NotFound<string>>> GetProjectAsync(RuntimeRegistry registry, string id, string projectRoot, string? tier = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (!TryParseMemoryId(id, out var memoryId)) return TypedResults.NotFound();
        if (IsLocalTier(tier))
        {
            var local = registry.TryResolveLocalServiceByScopeRoot(projectRoot, out var canonicalRoot);
            if (local is null) return TypedResults.NotFound();
            var localMemory = await local.GetByIdAsync(memoryId);
            return localMemory is null ? TypedResults.NotFound() : TypedResults.Ok(ScopedMemoryDto.From(localMemory, MemoryScopeKind.ProjectLocal, canonicalRoot));
        }
        var service = registry.TryResolveMemoryServiceByScopeRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found");
        var memory = await service.GetByIdAsync(memoryId);
        return memory is null ? TypedResults.NotFound() : TypedResults.Ok(ScopedMemoryDto.From(memory, MemoryScopeKind.Project, projectRoot));
    }

    private static async Task<Results<Created<ScopedMemoryDto>, BadRequest<string>, NotFound<string>>> CreateProjectAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, string projectRoot, SaveMemoryRequest request, string? tier = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (string.IsNullOrWhiteSpace(request.Content)) return TypedResults.BadRequest("Content is required.");
        if (IsLocalTier(tier))
        {
            MemoryType localType = MemoryType.Note;
            if (!string.IsNullOrEmpty(request.Type) && !Enum.TryParse<MemoryType>(request.Type, true, out localType))
                return TypedResults.BadRequest($"Invalid type '{request.Type}'");
            var local = registry.TryResolveLocalServiceByScopeRoot(projectRoot, out var canonicalRoot, createIfMissing: true);
            if (local is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found or not alive");
            var localMemory = new Memory(localType, request.Content, request.Tags, request.Source);
            var localSaved = await local.SaveAsync(localMemory);
            broadcaster.Notify("dashboard");
            var localDto = ScopedMemoryDto.From(localSaved, MemoryScopeKind.ProjectLocal, canonicalRoot);
            return TypedResults.Created($"/api/project/memories/{localSaved.Id}?projectRoot={Uri.EscapeDataString(projectRoot)}&tier=project-local", localDto);
        }
        var service = registry.TryResolveMemoryServiceByScopeRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found or not alive");
        MemoryType type = MemoryType.Note;
        if (!string.IsNullOrEmpty(request.Type) && !Enum.TryParse<MemoryType>(request.Type, true, out type))
            return TypedResults.BadRequest($"Invalid type '{request.Type}'");
        var memory = new Memory(type, request.Content, request.Tags, request.Source);
        var saved = await service.SaveAsync(memory);
        broadcaster.Notify("dashboard");
        var dto = ScopedMemoryDto.From(saved, MemoryScopeKind.Project, projectRoot);
        return TypedResults.Created($"/api/project/memories/{saved.Id}?projectRoot={Uri.EscapeDataString(projectRoot)}", dto);
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<string>, NotFound<string>>> DeleteProjectAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, string id, string projectRoot, string? tier = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
        if (!TryParseMemoryId(id, out var memoryId)) return TypedResults.NotFound();
        if (IsLocalTier(tier))
        {
            var local = registry.TryResolveLocalServiceByScopeRoot(projectRoot, out _);
            if (local is null) return TypedResults.NotFound();
            var localDeleted = await local.DeleteAsync(memoryId);
            if (localDeleted)
            {
                broadcaster.Notify("dashboard");
                return TypedResults.NoContent();
            }
            return TypedResults.NotFound();
        }
        var service = registry.TryResolveMemoryServiceByScopeRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found");
        var deleted = await service.DeleteAsync(memoryId);
        if (deleted)
        {
            broadcaster.Notify("dashboard");
            return TypedResults.NoContent();
        }
        return TypedResults.NotFound();
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>, NotFound<string>>> UpdateProjectAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, string id, string projectRoot, UpdateMemoryRequest request, string? tier = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return TypedResults.BadRequest("projectRoot is required");
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
        if (IsLocalTier(tier))
        {
            var local = registry.TryResolveLocalServiceByScopeRoot(projectRoot, out var canonicalRoot);
            if (local is null) return TypedResults.NotFound();
            var localUpdated = await local.UpdateAsync(memoryId, request.Content, type, request.Tags?.ToArray(), request.Source, status);
            if (localUpdated is null) return TypedResults.NotFound();
            broadcaster.Notify("dashboard");
            return TypedResults.Ok(ScopedMemoryDto.From(localUpdated, MemoryScopeKind.ProjectLocal, canonicalRoot));
        }
        var service = registry.TryResolveMemoryServiceByScopeRoot(projectRoot);
        if (service is null) return TypedResults.NotFound<string>($"Project '{projectRoot}' not found");
        var updated = await service.UpdateAsync(memoryId, request.Content, type, request.Tags?.ToArray(), request.Source, status);
        if (updated is null) return TypedResults.NotFound();
        broadcaster.Notify("dashboard");
        return TypedResults.Ok(ScopedMemoryDto.From(updated, MemoryScopeKind.Project, projectRoot));
    }

    // ---- Copy / Promote ----

    private static bool IsLocalScope(string? scope)
        => string.Equals(scope?.Trim(), "project-local", StringComparison.OrdinalIgnoreCase);

    private static IMemoryService? ResolveCopySource(
        RuntimeRegistry registry, string? sourceScope, string? sourceProjectRoot, out string? sourceCanonicalRoot)
    {
        sourceCanonicalRoot = null;
        if (string.Equals(sourceScope?.Trim(), "global", StringComparison.OrdinalIgnoreCase))
        {
            return registry.GetGlobalMemoryService();
        }
        if (string.IsNullOrWhiteSpace(sourceProjectRoot)) return null;
        if (IsLocalScope(sourceScope))
        {
            return registry.TryResolveLocalServiceByScopeRoot(sourceProjectRoot, out sourceCanonicalRoot);
        }
        return registry.TryResolveMemoryServiceByScopeRoot(sourceProjectRoot);
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> CopyToProjectAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, CopyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id)) return TypedResults.BadRequest("Id is required");
        if (string.IsNullOrWhiteSpace(request.TargetProjectRoot)) return TypedResults.BadRequest("TargetProjectRoot is required");
        if (!TryParseMemoryId(request.Id, out var memoryId)) return TypedResults.NotFound();

        var srcService = ResolveCopySource(registry, request.SourceScope, request.SourceProjectRoot, out _);
        if (srcService is null) return TypedResults.NotFound();
        var sourceMemory = await srcService.GetByIdAsync(memoryId);
        if (sourceMemory is null) return TypedResults.NotFound();

        var targetService = registry.TryResolveMemoryServiceByScopeRoot(request.TargetProjectRoot);
        if (targetService is null) return TypedResults.BadRequest($"Target project '{request.TargetProjectRoot}' not alive");

        var copy = request.Move
            ? new Memory(sourceMemory.Type, sourceMemory.Content, sourceMemory.Tags, sourceMemory.Source, sourceMemory.Status, sourceMemory.Id, sourceMemory.CreatedAt, DateTimeOffset.UtcNow)
            : new Memory(sourceMemory.Type, sourceMemory.Content, sourceMemory.Tags, sourceMemory.Source, sourceMemory.Status);
        var saved = await targetService.SaveAsync(copy);
        if (request.Move)
        {
            await srcService.DeleteAsync(memoryId);
        }
        broadcaster.Notify("dashboard");
        return TypedResults.Ok(ScopedMemoryDto.From(saved, MemoryScopeKind.Project, request.TargetProjectRoot));
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> MoveToProjectAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, CopyRequest request)
    {
        var moveRequest = request with { Move = true };
        return await CopyToProjectAsync(registry, broadcaster, moveRequest);
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> PromoteToGlobalAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, PromoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id)) return TypedResults.BadRequest("Id is required");
        if (string.IsNullOrWhiteSpace(request.SourceProjectRoot)) return TypedResults.BadRequest("SourceProjectRoot is required");
        if (!TryParseMemoryId(request.Id, out var memoryId)) return TypedResults.NotFound();
        var srcService = ResolveCopySource(registry, request.SourceScope, request.SourceProjectRoot, out _);
        if (srcService is null) return TypedResults.NotFound();
        var sourceMemory = await srcService.GetByIdAsync(memoryId);
        if (sourceMemory is null) return TypedResults.NotFound();
        var promoteCopy = request.Move
            ? new Memory(sourceMemory.Type, sourceMemory.Content, sourceMemory.Tags, sourceMemory.Source, sourceMemory.Status, sourceMemory.Id, sourceMemory.CreatedAt, DateTimeOffset.UtcNow)
            : new Memory(sourceMemory.Type, sourceMemory.Content, sourceMemory.Tags, sourceMemory.Source, sourceMemory.Status);
        var saved = await registry.GetGlobalMemoryService().SaveAsync(promoteCopy);
        if (request.Move)
        {
            // Move semantics: delete the source after the global copy is confirmed saved.
            await srcService.DeleteAsync(memoryId);
        }
        broadcaster.Notify("dashboard");
        return TypedResults.Ok(ScopedMemoryDto.From(saved, MemoryScopeKind.Global, null));
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> CopyToLocalAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, CopyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id)) return TypedResults.BadRequest("Id is required");
        if (string.IsNullOrWhiteSpace(request.TargetProjectRoot)) return TypedResults.BadRequest("TargetProjectRoot is required");
        if (!TryParseMemoryId(request.Id, out var memoryId)) return TypedResults.NotFound();
        var srcService = ResolveCopySource(registry, request.SourceScope, request.SourceProjectRoot, out _);
        if (srcService is null) return TypedResults.NotFound();
        var sourceMemory = await srcService.GetByIdAsync(memoryId);
        if (sourceMemory is null) return TypedResults.NotFound();

        var targetLocal = registry.TryResolveLocalServiceByScopeRoot(request.TargetProjectRoot, out var canonicalRoot, createIfMissing: true);
        if (targetLocal is null) return TypedResults.BadRequest($"Target project '{request.TargetProjectRoot}' not alive");
        var localCopy = request.Move
            ? new Memory(sourceMemory.Type, sourceMemory.Content, sourceMemory.Tags, sourceMemory.Source, sourceMemory.Status, sourceMemory.Id, sourceMemory.CreatedAt, DateTimeOffset.UtcNow)
            : new Memory(sourceMemory.Type, sourceMemory.Content, sourceMemory.Tags, sourceMemory.Source, sourceMemory.Status);
        var saved = await targetLocal.SaveAsync(localCopy);
        if (request.Move)
        {
            await srcService.DeleteAsync(memoryId);
        }
        broadcaster.Notify("dashboard");
        return TypedResults.Ok(ScopedMemoryDto.From(saved, MemoryScopeKind.ProjectLocal, canonicalRoot));
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> MoveToLocalAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, CopyRequest request)
    {
        var moveRequest = request with { Move = true };
        return await CopyToLocalAsync(registry, broadcaster, moveRequest);
    }

    private static async Task<Results<Ok<ScopedMemoryDto>, NotFound, BadRequest<string>>> MoveToGlobalAsync(RuntimeRegistry registry, MemoryChangeBroadcaster broadcaster, PromoteRequest request)
    {
        var moveRequest = request with { Move = true };
        return await PromoteToGlobalAsync(registry, broadcaster, moveRequest);
    }

    private static bool TryParseMemoryId(string id, out MemoryId memoryId)
    {
        try { memoryId = MemoryId.Parse(id); return true; } catch (ArgumentException) { memoryId = default; return false; }
    }
}
