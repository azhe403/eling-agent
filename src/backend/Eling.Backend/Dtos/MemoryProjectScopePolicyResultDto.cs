using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Result of <c>memory_project_scope_policy</c>. On success <see cref="Policy"/>
/// carries the full updated policy; on failure <see cref="Error"/> and
/// <see cref="Code"/> follow the <c>{ ok, error, code }</c> convention.
/// </summary>
public sealed class MemoryProjectScopePolicyResultDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("target")]
    public string Target { get; set; } = "project";

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = "";

    [JsonPropertyName("appliedTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppliedTo { get; set; }

    [JsonPropertyName("policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectScopePolicyDto? Policy { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }
}
