using SkillListMetadata = Eling.Backend.Dtos.SkillListMetadata;
using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Scope;
using Eling.Core.Skill;
using Eling.Core.Skill.Scanning;
using Eling.Core.Skill.Storage;

namespace Eling.Backend.Endpoints;

/// <summary>
/// REST surface for the skill catalog (spec §7). Machine-global like the
/// catalog itself — one group, no scope tiers. External skills refuse
/// content mutations (save, rename); delete-to-trash and the explicit
/// user-called adopt route work for external skills. Reads are
/// origin-agnostic. The envelope is applied by
/// <see cref="ApiEnvelopeMiddleware"/>; <c>/{id}</c> routes are active-catalog
/// only (a trashed skill answers 404) except restore, which resolves the
/// trashed row so a deleted skill can come back.
/// </summary>
public static class SkillEndpoints
{
    public static WebApplication MapSkillRoutes(this WebApplication app)
    {
        app.MapGroup("/api/skills").MapSkillEndpoints();
        return app;
    }

    private static RouteGroupBuilder MapSkillEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/skills?q=...&tag=...&origin=eling|external&page=1&pageSize=20&includeTrashed=false&sort=name
        group.MapGet("/", ListSkillsAsync);

        // GET /api/skills/{id}
        group.MapGet("/{id}", GetSkillAsync);

        // POST /api/skills
        group.MapPost("/", CreateSkillAsync);

        // PUT /api/skills/{id}
        group.MapPut("/{id}", UpdateSkillAsync);

        // DELETE /api/skills/{id}
        group.MapDelete("/{id}", DeleteSkillAsync);

        // POST /api/skills/{id}/restore
        group.MapPost("/{id}/restore", RestoreSkillAsync);

        // POST /api/skills/{id}/adopt
        group.MapPost("/{id}/adopt", AdoptSkillAsync);

        // POST /api/skills/rescan
        group.MapPost("/rescan", RescanCatalogAsync);

        return group;
    }

    private static async Task<IResult> ListSkillsAsync(
        ISkillService service,
        string? q,
        string? tag,
        string? origin,
        int? page,
        int? pageSize,
        bool includeTrashed = false,
        string? sort = null)
    {
        if (page is < 1)
        {
            return TypedResults.BadRequest("Query parameter 'page' must be at least 1.");
        }

        if (pageSize is < 1 or > 100)
        {
            return TypedResults.BadRequest("Query parameter 'pageSize' must be between 1 and 100.");
        }

        var wireSort = sort ?? "name";
        string serviceSort;
        if (wireSort == "name")
        {
            serviceSort = "name";
        }
        else if (wireSort == "updated")
        {
            serviceSort = "updated_at";
        }
        else
        {
            return TypedResults.BadRequest("Query parameter 'sort' must be \"name\" or \"updated\".");
        }

        string? originWire = null;
        if (!string.IsNullOrWhiteSpace(origin))
        {
            var trimmed = origin.Trim().ToLowerInvariant();
            if (trimmed is not ("eling" or "external"))
            {
                return TypedResults.BadRequest("Query parameter 'origin' must be \"eling\" or \"external\".");
            }

            originWire = trimmed;
        }

        var pageNumber = page ?? 1;
        var size = pageSize ?? 20;

        // The service pages, but the query/tag filters are applied on top in
        // memory — the same two-phase the MCP list uses — so fetch the whole
        // (already origin-filtered) set first, filter, then window it.
        SkillListResult everything;
        try
        {
            everything = await service.ListAsync(
                includeTrashed, originWire, page: 1, pageSize: int.MaxValue, sort: serviceSort);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }

        var filtered = everything.Items.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            filtered = filtered.Where(s =>
                s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                s.Body.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var needle = tag.Trim().ToLowerInvariant();
            filtered = filtered.Where(s => s.Tags.Contains(needle, StringComparer.OrdinalIgnoreCase));
        }

        var materialized = filtered.ToList();
        var offset = (int)Math.Min((long)(pageNumber - 1) * size, int.MaxValue);
        var items = materialized
            .Skip(offset)
            .Take(size)
            .Select(s => SkillListItemDto.From(s, SkillPath(s.Name)))
            .ToList();

        return TypedResults.Ok(new SkillListDto
        {
            Items = items,
            Metadata = new SkillListMetadata(everything.Total, materialized.Count, size, offset)
        });
    }

    private static async Task<IResult> GetSkillAsync(
        ISkillService service, HttpContext httpContext, string id)
    {
        if (!SkillId.TryParse(id, out var skillId))
        {
            return NotFoundSkill(httpContext, $"Skill id '{id}' is not valid.");
        }

        var skill = await service.GetByIdAsync(skillId);
        return skill is null
            ? NotFoundSkill(httpContext, $"No skill with id '{id}' exists.")
            : TypedResults.Ok(SkillDto.From(skill, SkillPath(skill.Name)));
    }

    private static async Task<IResult> CreateSkillAsync(
        ISkillService service, HttpContext httpContext, SaveSkillRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.BadRequest("Name is required.");
        }

        // Validate before ANY path is computed from the name — the folder path
        // below trusts it.
        if (!Skill.ValidateName(request.Name))
        {
            return TypedResults.BadRequest(
                $"Name must be lowercase kebab-case (^[a-z0-9]+(-[a-z0-9]+)*$) and at most {Skill.MaxNameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return TypedResults.BadRequest("Description is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return TypedResults.BadRequest("Body is required.");
        }

        try
        {
            var siblingFiles = SkillTools.PrepareSiblingFiles(
                request.SiblingFiles?.ToArray(),
                SkillCatalogPaths.SkillFolder(request.Name));
            var relatedMemories = SkillTools.ValidateRelatedReferences(
                request.RelatedMemories?.ToArray(), snippets: false);
            var relatedSnippets = SkillTools.ValidateRelatedReferences(
                request.RelatedSnippets?.ToArray(), snippets: true);

            var input = new SkillSaveInput(
                request.Name,
                request.Description,
                request.Body,
                request.Tags,
                relatedMemories,
                relatedSnippets,
                siblingFiles);

            var saved = await service.SaveAsync(input);
            return TypedResults.Created($"/api/skills/{saved.Id}", SkillDto.From(saved, SkillPath(saved.Name)));
        }
        catch (Exception ex) when (IsMappedSkillException(ex))
        {
            return MapSkillException(httpContext, ex);
        }
    }

    private static async Task<IResult> UpdateSkillAsync(
        ISkillService service, HttpContext httpContext, string id, UpdateSkillRequest request)
    {
        if (!SkillId.TryParse(id, out var skillId))
        {
            return NotFoundSkill(httpContext, $"Skill id '{id}' is not valid.");
        }

        var existing = await service.GetByIdAsync(skillId);
        if (existing is null)
        {
            return NotFoundSkill(httpContext, $"No skill with id '{id}' exists.");
        }

        if (existing.Origin == SkillOrigin.External)
        {
            return ConflictSkill(
                httpContext,
                "skill_read_only",
                $"Skill '{existing.Name}' is externally supplied (owned by its creator's tooling); Eling never writes external skills.");
        }

        try
        {
            var current = existing;
            if (request.Name is not null)
            {
                var newName = request.Name.Trim();
                if (newName.Length == 0)
                {
                    return TypedResults.BadRequest("Name must not be empty.");
                }

                if (!string.Equals(newName, existing.Name, StringComparison.Ordinal))
                {
                    // Rename first: the folder moves and the ULID is preserved,
                    // so the id in the route stays valid for the save below.
                    current = await service.RenameAsync(existing.Name, newName);
                }
            }

            if (request.Description is not null || request.Body is not null || request.Tags is not null)
            {
                var input = new SkillSaveInput(
                    current.Name,
                    request.Description ?? current.Description,
                    request.Body ?? current.Body,
                    request.Tags ?? current.Tags.ToList(),
                    current.RelatedMemories,
                    current.RelatedSnippets);

                current = await service.SaveAsync(input);
            }

            return TypedResults.Ok(SkillDto.From(current, SkillPath(current.Name)));
        }
        catch (Exception ex) when (IsMappedSkillException(ex))
        {
            return MapSkillException(httpContext, ex);
        }
    }

    private static async Task<IResult> DeleteSkillAsync(
        ISkillService service, HttpContext httpContext, string id)
    {
        if (!SkillId.TryParse(id, out var skillId))
        {
            return NotFoundSkill(httpContext, $"Skill id '{id}' is not valid.");
        }

        var existing = await service.GetByIdAsync(skillId);
        if (existing is null)
        {
            return NotFoundSkill(httpContext, $"No skill with id '{id}' exists.");
        }

        try
        {
            await service.DeleteAsync(existing.Name);
            return TypedResults.Ok(new SkillTrashedResponse { Trashed = true });
        }
        catch (Exception ex) when (IsMappedSkillException(ex))
        {
            return MapSkillException(httpContext, ex);
        }
    }

    private static async Task<IResult> RestoreSkillAsync(
        ISkillService service, HttpContext httpContext, string id)
    {
        if (!SkillId.TryParse(id, out var skillId))
        {
            return NotFoundSkill(httpContext, $"Skill id '{id}' is not valid.");
        }

        // The active-catalog lookup would miss trashed rows, and those are
        // exactly the ones restore exists for — resolve including the trash.
        var resolved = await ResolveIncludingTrashedAsync(service, skillId);
        if (resolved is null)
        {
            return NotFoundSkill(httpContext, $"No skill with id '{id}' exists.");
        }

        try
        {
            await service.RestoreAsync(resolved.Name);
        }
        catch (Exception ex) when (IsMappedSkillException(ex))
        {
            return MapSkillException(httpContext, ex);
        }

        var restored = await service.GetByIdAsync(skillId);
        return restored is null
            ? NotFoundSkill(httpContext, $"Skill with id '{id}' vanished after restore.")
            : TypedResults.Ok(SkillDto.From(restored, SkillPath(restored.Name)));
    }

    private static async Task<IResult> AdoptSkillAsync(
        ISkillService service, HttpContext httpContext, string id)
    {
        if (!SkillId.TryParse(id, out var skillId))
        {
            return NotFoundSkill(httpContext, $"Skill id '{id}' is not valid.");
        }

        // The active catalog is the adoption surface; a trashed skill answers
        // 400 with an instruction to restore it first (restore + adopt are
        // deliberately separate, explicit user actions).
        var existing = await service.GetByIdAsync(skillId);
        if (existing is null)
        {
            var trashed = await ResolveIncludingTrashedAsync(service, skillId);
            return trashed is null
                ? NotFoundSkill(httpContext, $"No skill with id '{id}' exists.")
                : TypedResults.BadRequest($"Skill '{trashed.Name}' is in the trash; restore it before adopting.");
        }

        try
        {
            var adopted = await service.AdoptAsync(existing.Name);
            var current = await service.GetByNameAsync(adopted.Name)
                ?? throw new SkillNotFoundException($"Skill '{adopted.Name}' vanished after adoption.");
            return TypedResults.Ok(SkillDto.From(current, SkillPath(current.Name)));
        }
        catch (Exception ex) when (IsMappedSkillException(ex))
        {
            return MapSkillException(httpContext, ex);
        }
    }

    private static async Task<IResult> RescanCatalogAsync(ISkillCatalogScanner scanner)
    {
        var report = await scanner.ScanAsync();
        var failures = report.Failures ?? Array.Empty<SkillScanFailure>();
        // Cap the wire payload: the failures list travels in the rescan
        // response envelope; 20 entries is plenty to act on, and the count
        // above already quantifies the damage.
        const int failuresCap = 20;
        return TypedResults.Ok(new
        {
            discovered = report.Discovered,
            external = report.External,
            missing = report.Missing,
            failed = report.Failed,
            renamed = report.Renamed,
            failures = failures
                .Take(failuresCap)
                .Select(f => new { path = f.Path, reason = f.Reason })
                .ToList(),
            failuresTruncated = failures.Count > failuresCap
        });
    }

    /// <summary>
    /// Resolves an id to a skill across the active catalog and the trash.
    /// Only restore uses this; the other routes stay active-catalog.
    /// </summary>
    private static async Task<Skill?> ResolveIncludingTrashedAsync(ISkillService service, SkillId id)
    {
        var all = await service.ListAsync(
            includeTrashed: true, origin: null, page: 1, pageSize: int.MaxValue, sort: "name");
        return all.Items.FirstOrDefault(s => s.Id == id);
    }

    private static string SkillPath(string name) => Path.GetFullPath(
        Path.Combine(SkillCatalogPaths.SkillsDirectory, name, "SKILL.md"));

    private static bool IsMappedSkillException(Exception ex) => ex is
        SkillReadOnlyException or
        SkillNotFoundException or
        SkillConflictException or
        SkillValidationException or
        ArgumentException;

    private static IResult MapSkillException(HttpContext httpContext, Exception ex) => ex switch
    {
        SkillReadOnlyException e => ConflictSkill(httpContext, "skill_read_only", e.Message),
        SkillConflictException e => ConflictSkill(httpContext, "skill_conflict", e.Message),
        SkillNotFoundException e => NotFoundSkill(httpContext, e.Message),
        SkillValidationException e => TypedResults.BadRequest(e.Message),
        ArgumentException e => TypedResults.BadRequest(e.Message),
        _ => throw ex,
    };

    private static IResult NotFoundSkill(HttpContext httpContext, string message)
    {
        httpContext.Items[ApiEnvelopeMiddleware.ErrorCodeItem] = "skill_not_found";
        return TypedResults.NotFound(message);
    }

    private static IResult ConflictSkill(HttpContext httpContext, string code, string message)
    {
        httpContext.Items[ApiEnvelopeMiddleware.ErrorCodeItem] = code;
        return TypedResults.Conflict(message);
    }
}
