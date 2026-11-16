using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Paging metadata for the skill list. Two counts travel with the window:
/// the size of the whole catalog including trashed rows, and how many rows
/// survive the request's origin/query/tag filters - so a client never has
/// to guess either number from the page it received.
/// </summary>
/// <param name="Total">
/// How many skills the catalog holds including trashed rows, ignoring the
/// origin filter - the selection the page is drawn from.
/// </param>
/// <param name="Matching">
/// How many rows match the request's origin/query/tag filters before the
/// page window is applied - the count the client should show as "showing".
/// </param>
/// <param name="Limit">The page size the caller asked for.</param>
/// <param name="Offset">The zero-based offset of the first row in the page.</param>
public sealed record SkillListMetadata(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("matching")] int Matching,
    [property: JsonPropertyName("limit")] int? Limit,
    [property: JsonPropertyName("offset")] int? Offset);

/// <summary>
/// The skill list envelope: <see cref="Metadata"/> plus the
/// <see cref="Items"/> for the requested window. Items carry the compact
/// shape (no body); total/matching keep the same meaning everywhere -
/// MCP skill_list, GET /api/skills - so the two surfaces cannot drift.
/// </summary>
public sealed record SkillListDto
{
    [JsonPropertyName("items")]
    public IReadOnlyList<SkillListItemDto> Items { get; init; } = [];

    [JsonPropertyName("metadata")]
    public SkillListMetadata Metadata { get; init; } = new(0, 0, null, null);

    public static SkillListDto From(IReadOnlyList<SkillListItemDto> items, int total, int matching, int? limit, int? offset) => new()
    {
        Items = items,
        Metadata = new SkillListMetadata(total, matching, limit, offset)
    };
}
