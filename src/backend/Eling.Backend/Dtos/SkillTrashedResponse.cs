using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Envelope returned by the skill_delete MCP tool: the skill was moved to
/// the catalog trash and can be brought back with skill_restore.
/// </summary>
public sealed class SkillTrashedResponse
{
    [JsonPropertyName("trashed")]
    public bool Trashed { get; set; } = true;
}
