using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Page envelope returned by the skill_list MCP tool. Items is the page
/// window requested by the caller; Total is the size of the whole catalog
/// including trashed rows; Matching is the number of rows that passed the
/// request's tag/origin filter before the page window was applied.
/// </summary>
public sealed class SkillListDto
{
    [JsonPropertyName("items")]
    public IReadOnlyList<SkillListItemDto> Items { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("matching")]
    public int Matching { get; set; }

    public static SkillListDto From(IReadOnlyList<SkillListItemDto> items, int total, int matching) => new()
    {
        Items = items,
        Total = total,
        Matching = matching
    };
}
