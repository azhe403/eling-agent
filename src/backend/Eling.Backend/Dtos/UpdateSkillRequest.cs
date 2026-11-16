namespace Eling.Backend.Dtos;

/// <summary>
/// Payload for PUT /api/skills/{id}. Every field is optional; a changed
/// <paramref name="Name"/> triggers a rename (folder moves, id and
/// createdAt are preserved). Eling-managed skills only.
/// </summary>
public sealed record UpdateSkillRequest(
    string? Name = null,
    string? Description = null,
    string? Body = null,
    List<string>? Tags = null);
