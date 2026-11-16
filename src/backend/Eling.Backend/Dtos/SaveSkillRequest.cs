namespace Eling.Backend.Dtos;

/// <summary>
/// Payload for POST /api/skills. The name must be lowercase kebab-case;
/// <paramref name="SiblingFiles"/> carries extra payload files verbatim
/// (relative paths, Base64 contents), exactly like the skill_save MCP tool.
/// </summary>
public sealed record SaveSkillRequest(
    string Name,
    string Description,
    string Body,
    List<string>? Tags = null,
    List<SkillRelatedReferenceInput>? RelatedMemories = null,
    List<SkillRelatedReferenceInput>? RelatedSnippets = null,
    List<SkillSiblingFileInput>? SiblingFiles = null);
