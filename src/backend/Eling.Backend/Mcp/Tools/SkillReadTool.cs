using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core.Skill;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// MCP tools for the read side of the skill catalog: get by id or name,
/// FTS search across both origins, and paginated listing. All
/// machine-global; there is no scope parameter. Search and list cover
/// externally-supplied skills as well — discovery is origin-agnostic; only
/// mutation is restricted.
/// </summary>
[McpServerToolType]
public sealed class SkillReadTool
{
    private const int TagFilterFetchLimit = 256;

    private readonly ISkillService _skills;
    private readonly string? _catalogRoot;
    private readonly ILogger<SkillReadTool>? _logger;

    public SkillReadTool(ISkillService skills, string? catalogRoot = null, ILogger<SkillReadTool>? logger = null)
    {
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _catalogRoot = catalogRoot;
        _logger = logger;
    }

    [McpServerTool(Name = "skill_get"), Description("Retrieve a skill by its ULID or by its name. Returns the full skill: frontmatter-equivalent fields (id, name, description, tags, related memories/snippets), the Markdown body, the absolute SKILL.md path, and the origin ('eling' for Eling-managed, 'external' for externally-supplied skills, which Eling never writes). Use when you know exactly which skill you want; otherwise discover it with skill_search or skill_list. Returns null when no skill matches. Works for both origins.")]
    public async Task<SkillDto?> GetAsync(
        [Description("The skill's ULID or its lowercase kebab-case name. A ULID (26-char Crockford base32) resolves by id; anything else is treated as a name.")] string idOrName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(idOrName))
            {
                throw new SkillValidationException("idOrName must not be empty.");
            }

            var trimmed = idOrName.Trim();
            Skill? skill = null;
            if (SkillId.TryParse(trimmed, out var id))
            {
                skill = await _skills.GetByIdAsync(id);
            }

            skill ??= await _skills.GetByNameAsync(trimmed);

            _logger?.LogInformation("skill_get '{IdOrName}' found: {Found}", trimmed, skill is not null);
            return skill is null ? null : SkillDto.From(skill, SkillTools.MarkdownPath(_catalogRoot, skill.Name));
        }
        catch (Exception ex)
        {
            var mapped = SkillTools.MapException(ex);
            if (ReferenceEquals(mapped, ex))
            {
                _logger?.LogError(ex, "skill_get threw an exception (idOrName={IdOrName})", idOrName);
            }
            else
            {
                _logger?.LogWarning(ex, "skill_get rejected (idOrName={IdOrName}): {Message}", idOrName, ex.Message);
            }

            throw mapped;
        }
    }

    [McpServerTool(Name = "skill_search"), Description("Search the skill catalog with full-text search over skill name, description, and body. Returns ranked hits (BM25, higher score = better match) with name, description, absolute SKILL.md path, origin, and score — enough to decide whether to read the full skill, without inlining every body. Covers BOTH origins: 'eling' skills (managed by Eling) and 'external' skills (supplied by other tooling, read-only to Eling). Use when looking for a skill for a task; follow up with skill_get or read the SKILL.md path to load the full instructions. Filter by origin to see only one side; filter by tag to narrow to a topic. Trashed skills are never returned.")]
    public async Task<IReadOnlyList<SkillSearchResultDto>> SearchAsync(
        [Description("The search query — keywords to match against skill name, description, and body. Multi-word queries are AND-combined with a broader OR fallback.")] string query,
        [Description("Optional tag filter; only skills carrying this tag are returned (case-insensitive), applied after the FTS ranking.")] string? tag = null,
        [Description("Optional origin filter: 'eling' (Eling-managed) or 'external' (externally-supplied). Omit to search both.")] string? origin = null,
        [Description("Maximum number of hits to return. Defaults to 10.")] int limit = 10)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new SkillValidationException("Query must not be empty.");
            }

            if (limit < 1)
            {
                throw new SkillValidationException("Limit must be at least 1.");
            }

            var trimmedOrigin = NormalizeOriginOrThrow(origin);
            var trimmedTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim().ToLowerInvariant();

            // The tag filter is applied after the FTS pass, so an unfiltered
            // limit would starve tag-filtered searches; over-fetch when one is set.
            var fetchLimit = trimmedTag is null ? limit : Math.Max(limit, TagFilterFetchLimit);
            var hits = await _skills.SearchAsync(query.Trim(), trimmedOrigin, fetchLimit);
            if (hits.Count == 0)
            {
                return [];
            }

            // Hits carry ids only; hydrate from a single full active-catalog
            // listing instead of one scan per hit.
            var inventory = await _skills.ListAsync(
                includeTrashed: false, origin: null, page: 1, pageSize: int.MaxValue, sort: "name");
            var byId = new Dictionary<SkillId, Skill>();
            foreach (var skill in inventory.Items)
            {
                byId[skill.Id] = skill;
            }

            var results = new List<SkillSearchResultDto>(Math.Min(hits.Count, limit));
            foreach (var hit in hits)
            {
                if (!byId.TryGetValue(hit.Id, out var skill))
                {
                    continue;
                }

                if (trimmedTag is not null
                    && !skill.Tags.Contains(trimmedTag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(SkillSearchResultDto.From(skill, hit));
                if (results.Count >= limit)
                {
                    break;
                }
            }

            _logger?.LogInformation("skill_search '{Query}' returned {Count} hits (origin={Origin}, tag={Tag})",
                query, results.Count, trimmedOrigin ?? "(both)", trimmedTag ?? "(any)");
            return results;
        }
        catch (Exception ex)
        {
            var mapped = SkillTools.MapException(ex);
            if (ReferenceEquals(mapped, ex))
            {
                _logger?.LogError(ex, "skill_search threw an exception (query={Query})", query);
            }
            else
            {
                _logger?.LogWarning(ex, "skill_search rejected (query={Query}): {Message}", query, ex.Message);
            }

            throw mapped;
        }
    }

    [McpServerTool(Name = "skill_list"), Description("List the skill catalog with pagination and optional filters. Returns { items, metadata: { total, matching, limit, offset } }: items is the requested page (compact entries with id, name, description, absolute SKILL.md path, origin, tags, and updatedAt); metadata.total is the size of the whole catalog including trashed rows; metadata.matching is how many rows passed the tag/origin filter before the page window was applied. Use to browse the catalog or to find a skill by tag; use skill_search to find one by keyword instead. Filter by origin to see only 'eling' or 'external' skills; set includeTrashed to also list skills currently in the trash (their path points at the former active location). Sorts by name (default) or updated_at (most recently updated first).")]
    public async Task<SkillListDto> ListAsync(
        [Description("Optional tag filter: only skills carrying this tag are listed (case-insensitive).")] string? tag = null,
        [Description("Optional origin filter: 'eling' (Eling-managed) or 'external' (externally-supplied). Omit to list both.")] string? origin = null,
        [Description("Include skills currently in the trash. Defaults to false.")] bool includeTrashed = false,
        [Description("1-based page number. Defaults to 1.")] int page = 1,
        [Description("Page size (at least 1). Defaults to 20.")] int pageSize = 20,
        [Description("Sort order: 'name' (default, ascending) or 'updated_at' (most recently updated first).")] string sort = "name")
    {
        try
        {
            if (page < 1)
            {
                throw new SkillValidationException("Page must be at least 1.");
            }

            if (pageSize < 1)
            {
                throw new SkillValidationException("PageSize must be at least 1.");
            }

            var trimmedOrigin = NormalizeOriginOrThrow(origin);

            if (sort is not ("name" or "updated_at"))
            {
                throw new SkillValidationException($"Sort must be \"name\" or \"updated_at\", not \"{sort}\".");
            }

            var trimmedTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim().ToLowerInvariant();

            var everything = await _skills.ListAsync(
                includeTrashed: includeTrashed, origin: trimmedOrigin, page: 1, pageSize: int.MaxValue, sort: sort);

            IReadOnlyList<Skill> matching = everything.Items;
            if (trimmedTag is not null)
            {
                matching = matching
                    .Where(s => s.Tags.Contains(trimmedTag, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            var offset = Math.Min((long)(page - 1) * pageSize, int.MaxValue);
            var items = matching
                .Skip((int)offset)
                .Take(pageSize)
                .Select(s => SkillListItemDto.From(s, SkillTools.MarkdownPath(_catalogRoot, s.Name)))
                .ToList();

            _logger?.LogInformation(
                "skill_list page {Page} returned {Count} of {Matching} matching skills (total {Total})",
                page, items.Count, matching.Count, everything.Total);
            return SkillListDto.From(items, everything.Total, matching.Count, pageSize, (int)offset);
        }
        catch (Exception ex)
        {
            var mapped = SkillTools.MapException(ex);
            if (ReferenceEquals(mapped, ex))
            {
                _logger?.LogError(ex, "skill_list threw an exception (origin={Origin}, page={Page}, pageSize={PageSize})", origin, page, pageSize);
            }
            else
            {
                _logger?.LogWarning(ex, "skill_list rejected (origin={Origin}, page={Page}, pageSize={PageSize}): {Message}", origin, page, pageSize, ex.Message);
            }

            throw mapped;
        }
    }

    private static string? NormalizeOriginOrThrow(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        var trimmed = origin.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "eling" => trimmed,
            "external" => trimmed,
            _ => throw new SkillValidationException($"Origin '{origin}' must be \"eling\" or \"external\".")
        };
    }
}
