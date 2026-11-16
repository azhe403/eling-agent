using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// One cross-reference supplied to skill_save: an id plus (for memory
/// references) the memory's scope label and the relation vocabulary term.
/// Snippet references omit the scope.
/// </summary>
public sealed class SkillRelatedReferenceDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; set; }

    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "";
}
