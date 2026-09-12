using System.Text.Json.Serialization;
using Eling.Core.Scope;

namespace Eling.Backend.Dtos;

/// <summary>
/// Wire shape of a project-scope policy, echoed by the
/// <c>memory_project_scope_policy</c> tool. Nested types keep the file to one
/// top-level type while matching the on-disk JSON structure.
/// </summary>
public sealed class ProjectScopePolicyDto
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("default")]
    public string Default { get; set; } = "ask";

    [JsonPropertyName("projects")]
    public Dictionary<string, EntryDto> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("patterns")]
    public List<PatternDto> Patterns { get; set; } = [];

    public static ProjectScopePolicyDto From(ProjectScopePolicy policy) => new()
    {
        CreatedAt = policy.CreatedAt,
        UpdatedAt = policy.UpdatedAt,
        Default = Format(policy.Default),
        Projects = policy.Projects.ToDictionary(
            pair => pair.Key,
            pair => new EntryDto
            {
                Decision = Format(pair.Value.Decision),
                CreatedAt = pair.Value.CreatedAt,
                UpdatedAt = pair.Value.UpdatedAt
            },
            StringComparer.OrdinalIgnoreCase),
        Patterns = policy.Patterns
            .Select(pattern => new PatternDto
            {
                Glob = pattern.Glob,
                Decision = Format(pattern.Entry.Decision),
                CreatedAt = pattern.Entry.CreatedAt,
                UpdatedAt = pattern.Entry.UpdatedAt
            })
            .ToList()
    };

    private static string Format(ProjectScopeDecision decision)
        => decision == ProjectScopeDecision.Disabled ? "disabled" : "ask";

    public sealed class EntryDto
    {
        [JsonPropertyName("decision")]
        public string Decision { get; set; } = "ask";

        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    public sealed class PatternDto
    {
        [JsonPropertyName("glob")]
        public string Glob { get; set; } = "";

        [JsonPropertyName("decision")]
        public string Decision { get; set; } = "ask";

        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
