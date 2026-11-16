using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core.Skill;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// MCP tools for the write side of the skill catalog: save (create-or-update
/// by name), delete (move to trash), restore, adopt (explicit user-requested
/// conversion of an external skill to Eling-managed, in place). All
/// machine-global; there is no scope parameter. Content mutations (save) on
/// external skills are refused — the service enforces it, the tools only
/// surface it. Delete and restore work for both origins: an external skill is
/// moved to the trash, never edited. Adopt is the one path that re-claims an
/// external skill.
/// </summary>
[McpServerToolType]
public sealed class SkillWriteTool
{
    private readonly ISkillService _skills;
    private readonly string? _catalogRoot;
    private readonly ILogger<SkillWriteTool>? _logger;

    public SkillWriteTool(ISkillService skills, string? catalogRoot = null, ILogger<SkillWriteTool>? logger = null)
    {
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _catalogRoot = catalogRoot;
        _logger = logger;
    }

    [McpServerTool(Name = "skill_save"), Description("Save a skill to the machine-global skill catalog (~/.agents/skills/{name}/SKILL.md). Skills are reusable, multi-step workflows worth packaging as agent instructions — a repeatable procedure (build, test, release, review flow), not a single rule or fact. Call this when the user approves turning a workflow into a skill, or explicitly asks to create/update one. The name is both the folder name and the identity: it MUST be lowercase kebab-case (^[a-z0-9]+(-[a-z0-9]+)*$, at most 128 chars); anything else is rejected. Create-or-update semantics are by NAME: saving with an existing name replaces that skill's content but keeps its ULID and createdAt. Names owned by an externally-supplied skill are rejected — external skills are owned by their creator's tooling and Eling never writes them. Pass siblingFiles to ship scripts/references next to SKILL.md: each entry is { relativePath (forward slashes, relative to the skill folder, no '..', no absolute paths), contentBase64 }. Every sibling entry is validated and Base64-decoded before any filesystem write; a bad entry fails the whole save with nothing written. Related references use the shared vocabularies: relations implements|anti-pattern|example|test-case|reference; relatedMemories need a scope of global|project|project-local, relatedSnippets must NOT carry a scope.")]
    public async Task<SaveSkillResponse> SaveAsync(
        [Description("The skill's name — lowercase kebab-case (^[a-z0-9]+(-[a-z0-9]+)*$), at most 128 characters. Doubles as the folder name; stable identity is the ULID in frontmatter.")] string name,
        [Description("One-line summary of what the skill does and when to use it. Shown in search results and listings; at most 512 characters.")] string description,
        [Description("The skill instructions as Markdown — the full procedure the agent should follow when the skill is used. Be specific and self-contained; this is the text an agent reads.")] string body,
        [Description("Optional lowercase tags for categorization and filtering in skill_search / skill_list, e.g. ['blazor', 'forms']. Hyphens are allowed; tokens shorter than 2 characters are dropped.")] string[]? tags = null,
        [Description("Optional cross-references to memories this skill implements, exemplifies, or is an anti-pattern of. Each entry is { id, scope, relation } where scope is global|project|project-local and relation is one of implements|anti-pattern|example|test-case|reference.")] SkillRelatedReferenceInput[]? relatedMemories = null,
        [Description("Optional cross-references to snippets this skill relates to. Each entry is { id, relation }; snippet references must NOT carry a scope. Relation is one of implements|anti-pattern|example|test-case|reference.")] SkillRelatedReferenceInput[]? relatedSnippets = null,
        [Description("Optional extra files written verbatim next to SKILL.md, e.g. scripts or reference documents. Each entry is { relativePath, contentBase64 }; relativePath is relative to the skill folder, uses forward slashes, and may not be absolute, contain '..', drive-qualified segments, '~', or collide with SKILL.md. contentBase64 is the file content encoded as Base64. All entries are validated and decoded before any write; a failure leaves no partial skill behind.")] SkillSiblingFileInput[]? siblingFiles = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new SkillValidationException("Skill name must not be empty.");
            }

            var trimmedName = name.Trim();
            if (!Skill.ValidateName(trimmedName))
            {
                throw new SkillValidationException(
                    $"Skill name '{name}' must be lowercase kebab-case (^[a-z0-9]+(-[a-z0-9]+)*$) and at most {Skill.MaxNameLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                throw new SkillValidationException("Description must not be empty.");
            }

            if (description.Length > Skill.MaxDescriptionLength)
            {
                throw new SkillValidationException($"Description must be at most {Skill.MaxDescriptionLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                throw new SkillValidationException("Body must not be empty.");
            }

            var memories = SkillTools.ValidateRelatedReferences(relatedMemories, snippets: false);
            var snippets = SkillTools.ValidateRelatedReferences(relatedSnippets, snippets: true);
            var skillFolder = SkillTools.SkillFolderPath(_catalogRoot, trimmedName);
            var siblings = SkillTools.PrepareSiblingFiles(siblingFiles, skillFolder);

            var saved = await _skills.SaveAsync(
                new SkillSaveInput(trimmedName, description, body, tags, memories, snippets, siblings));

            _logger?.LogInformation("Saved skill '{Name}' (id {Id})", saved.Name, saved.Id);
            return SaveSkillResponse.From(saved, SkillTools.MarkdownPath(_catalogRoot, saved.Name));
        }
        catch (Exception ex)
        {
            var mapped = SkillTools.MapException(ex);
            if (ReferenceEquals(mapped, ex))
            {
                _logger?.LogError(ex, "skill_save threw an exception (name={Name})", name);
            }
            else
            {
                _logger?.LogWarning(ex, "skill_save rejected (name={Name}): {Message}", name, ex.Message);
            }

            throw mapped;
        }
    }

    [McpServerTool(Name = "skill_delete"), Description("Move a skill to the catalog trash (~/.agents/.trash/skills/{name}). Nothing is permanently deleted; the skill can be brought back with skill_restore. Use when the user asks to delete, retire, or remove a skill from the catalog. Works for both eling-managed and externally-supplied skills: an external skill's folder is moved to the trash untouched — never edited, renamed or merged — and can be restored the same way. Deleting fails when the skill does not exist, or when the trash destination is occupied by an earlier-trashed skill of the same name. Editing or renaming an external skill remains forbidden. A trashed name stays blocked for re-creation until it is restored or purged.")]
    public async Task<SkillTrashedResponse> DeleteAsync(
        [Description("The name of the skill to move to the trash. Must be a lowercase kebab-case skill name.")] string name)
    {
        try
        {
            var trimmedName = ValidateNameOrThrow(name);
            await _skills.DeleteAsync(trimmedName);
            _logger?.LogInformation("Moved skill '{Name}' to the trash", trimmedName);
            return new SkillTrashedResponse();
        }
        catch (Exception ex)
        {
            var mapped = SkillTools.MapException(ex);
            if (ReferenceEquals(mapped, ex))
            {
                _logger?.LogError(ex, "skill_delete threw an exception (name={Name})", name);
            }
            else
            {
                _logger?.LogWarning(ex, "skill_delete rejected (name={Name}): {Message}", name, ex.Message);
            }

            throw mapped;
        }
    }

    [McpServerTool(Name = "skill_adopt"), Description("Convert an externally-supplied skill (origin external) to an Eling-managed one, in place: the folder and SKILL.md stay where they are, only the frontmatter gains Eling keys (id, createdAt, updatedAt) and the index row flips to origin 'eling'. This is an explicit, permanent user-requested adoption — call it only when the user asks to adopt, convert or take over ownership of a skill. Refuses unparseable frontmatter (no partial adoption; the error names the parse failure — fix the frontmatter YAML first), refuses an already-Eling-managed skill, a trashed skill, and an unknown name. The adopted skill becomes a full Eling-managed skill: skill_save and skill_rename work on it and it can never be externally owned again.")]
    public async Task<SkillDto> AdoptAsync(
        [Description("The name of the externally-supplied skill to adopt. Must be a lowercase kebab-case skill name.")] string name)
    {
        try
        {
            var trimmedName = ValidateNameOrThrow(name);
            var adopted = await _skills.AdoptAsync(trimmedName);
            var skill = await _skills.GetByNameAsync(trimmedName)
                ?? throw new SkillNotFoundException($"Skill '{trimmedName}' vanished after adoption.");
            _logger?.LogInformation("Adopted skill '{Name}' (id {Id}) to Eling management", skill.Name, skill.Id);
            return SkillDto.From(skill, SkillTools.MarkdownPath(_catalogRoot, skill.Name));
        }
        catch (Exception ex)
        {
            var mapped = SkillTools.MapException(ex);
            if (ReferenceEquals(mapped, ex))
            {
                _logger?.LogError(ex, "skill_adopt threw an exception (name={Name})", name);
            }
            else
            {
                _logger?.LogWarning(ex, "skill_adopt rejected (name={Name}): {Message}", name, ex.Message);
            }

            throw mapped;
        }
    }

    [McpServerTool(Name = "skill_restore"), Description("Restore a skill from the catalog trash (~/.agents/.trash/skills/{name}) back into the active catalog (~/.agents/skills/{name}) and re-index it. Use when the user wants back a skill that was trashed with skill_delete. Works for both eling-managed and externally-supplied skills — an externally-supplied skill is moved back untouched and its index row is re-activated. Fails when no trashed folder exists for the name.")]
    public async Task<SkillDto> RestoreAsync(
        [Description("The name of the trashed skill to restore. Must be a lowercase kebab-case skill name.")] string name)
    {
        try
        {
            var trimmedName = ValidateNameOrThrow(name);
            await _skills.RestoreAsync(trimmedName);
            var restored = await _skills.GetByNameAsync(trimmedName)
                ?? throw new SkillNotFoundException($"Skill '{trimmedName}' was restored but could not be read back.");
            _logger?.LogInformation("Restored skill '{Name}' from the trash", trimmedName);
            return SkillDto.From(restored, SkillTools.MarkdownPath(_catalogRoot, restored.Name));
        }
        catch (Exception ex)
        {
            var mapped = SkillTools.MapException(ex);
            if (ReferenceEquals(mapped, ex))
            {
                _logger?.LogError(ex, "skill_restore threw an exception (name={Name})", name);
            }
            else
            {
                _logger?.LogWarning(ex, "skill_restore rejected (name={Name}): {Message}", name, ex.Message);
            }

            throw mapped;
        }
    }

    private static string ValidateNameOrThrow(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new SkillValidationException("Skill name must not be empty.");
        }

        var trimmed = name.Trim();
        if (!Skill.ValidateName(trimmed))
        {
            throw new SkillValidationException(
                $"Skill name '{name}' must be lowercase kebab-case (^[a-z0-9]+(-[a-z0-9]+)*$) and at most {Skill.MaxNameLength} characters.");
        }

        return trimmed;
    }
}
