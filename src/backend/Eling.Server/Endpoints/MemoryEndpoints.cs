using Eling.Application;
using Eling.Core;
using Eling.Index;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Eling.Server.Endpoints;

public static class MemoryEndpoints
{
    public static WebApplication MapMemoryRoutes(this WebApplication app)
    {
        app.MapGroup("/api/memories").MapMemoryEndpoints();
        return app;
    }

    private static RouteGroupBuilder MapMemoryEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/memories?status=active
        group.MapGet("/", ListMemoriesAsync);

        // GET /api/memories/search?q=...&limit=10
        group.MapGet("/search", SearchMemoriesAsync);

        // GET /api/memories/{id}
        group.MapGet("/{id}", GetMemoryAsync);

        // POST /api/memories
        group.MapPost("/", CreateMemoryAsync);

        // DELETE /api/memories/{id}
        group.MapDelete("/{id}", DeleteMemoryAsync);

        // PATCH /api/memories/{id}
        group.MapPatch("/{id}", UpdateMemoryAsync);

        // POST /api/memories/rebuild-index
        group.MapPost("/rebuild-index", RebuildIndexAsync);

        return group;
    }

    private static async Task<Results<Ok<IReadOnlyCollection<Memory>>, BadRequest<string>>> ListMemoriesAsync(
        IMemoryService service,
        string? status,
        string? type,
        int? limit,
        int? offset
    )
    {
        var all = await service.ListAllAsync();
        // Newest first so limit/offset paging surfaces recent memories
        all = all.OrderByDescending(m => m.CreatedAt).ToList();

        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<MemoryStatus>(status, ignoreCase: true, out var parsed))
                return TypedResults.BadRequest($"Invalid status '{status}'. Valid: Active, Superseded, Archived");

            all = all.Where(m => m.Status == parsed).ToList();
        }

        if (!string.IsNullOrEmpty(type))
        {
            if (!Enum.TryParse<MemoryType>(type, ignoreCase: true, out var parsed))
                return TypedResults.BadRequest($"Invalid type '{type}'. Valid: Fact, Preference, Decision, Lesson, Note");

            all = all.Where(m => m.Type == parsed).ToList();
        }

        if (limit is < 1 or > 100)
            return TypedResults.BadRequest("Query parameter 'limit' must be between 1 and 100.");

        if (offset is < 0)
            return TypedResults.BadRequest("Query parameter 'offset' must be zero or greater.");

        if (offset is > 0)
            all = all.Skip(offset.Value).ToList();

        if (limit is not null)
            all = all.Take(limit.Value).ToList();

        return TypedResults.Ok((IReadOnlyCollection<Memory>)all);
    }

    private static async Task<Results<Ok<IReadOnlyCollection<MemorySearchResult>>, BadRequest<string>>> SearchMemoriesAsync(
        IMemoryService service,
        string q,
        int? limit
    )
    {
        if (string.IsNullOrWhiteSpace(q))
            return TypedResults.BadRequest("Query parameter 'q' is required.");

        var results = await service.SearchAsync(q);
        var count = limit ?? 10;

        var limited = results.Take(count).ToList();
        return TypedResults.Ok((IReadOnlyCollection<MemorySearchResult>)limited);
    }

    private static async Task<Results<Ok<Memory>, NotFound>> GetMemoryAsync(
        IMemoryService service,
        string id
    )
    {
        if (!TryParseMemoryId(id, out var memoryId))
            return TypedResults.NotFound();

        var memory = await service.GetByIdAsync(memoryId);
        return memory is not null ? TypedResults.Ok(memory) : TypedResults.NotFound();
    }

    private static async Task<Results<Created<Memory>, BadRequest<string>>> CreateMemoryAsync(
        IMemoryService service,
        Dtos.SaveMemoryRequest request
    )
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return TypedResults.BadRequest("Content is required.");

        MemoryType type = MemoryType.Note;
        if (!string.IsNullOrEmpty(request.Type))
        {
            if (!Enum.TryParse<MemoryType>(request.Type, ignoreCase: true, out type))
                return TypedResults.BadRequest($"Invalid type '{request.Type}'. Valid: Fact, Preference, Decision, Lesson, Note");
        }

        var memory = new Memory(type, request.Content, request.Tags, request.Source);
        var saved = await service.SaveAsync(memory);
        return TypedResults.Created($"/api/memories/{saved.Id}", saved);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteMemoryAsync(
        IMemoryService service,
        string id
    )
    {
        if (!TryParseMemoryId(id, out var memoryId))
            return TypedResults.NotFound();

        var deleted = await service.DeleteAsync(memoryId);
        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<Memory>, NotFound, BadRequest<string>>> UpdateMemoryAsync(
        IMemoryService service,
        string id,
        Dtos.UpdateMemoryRequest request
    )
    {
        if (!TryParseMemoryId(id, out var memoryId))
            return TypedResults.NotFound();

        MemoryType? type = null;
        if (!string.IsNullOrEmpty(request.Type))
        {
            if (!Enum.TryParse<MemoryType>(request.Type, ignoreCase: true, out var parsed))
                return TypedResults.BadRequest($"Invalid type '{request.Type}'. Valid: Fact, Preference, Decision, Lesson, Note");

            type = parsed;
        }

        MemoryStatus? status = null;
        if (!string.IsNullOrEmpty(request.Status))
        {
            if (!Enum.TryParse<MemoryStatus>(request.Status, ignoreCase: true, out var parsed))
                return TypedResults.BadRequest($"Invalid status '{request.Status}'. Valid: Active, Superseded, Archived");

            status = parsed;
        }

        var updated = await service.UpdateAsync(
            memoryId,
            content: request.Content,
            type: type,
            tags: request.Tags?.ToArray(),
            source: request.Source,
            status: status);

        return updated is not null ? TypedResults.Ok(updated) : TypedResults.NotFound();
    }

    private static async Task<NoContent> RebuildIndexAsync(IMemoryService service)
    {
        await service.RebuildIndexAsync();
        return TypedResults.NoContent();
    }

    private static bool TryParseMemoryId(string id, out MemoryId memoryId)
    {
        try
        {
            memoryId = MemoryId.Parse(id);
            return true;
        }
        catch (ArgumentException)
        {
            memoryId = default;
            return false;
        }
    }
}