namespace Eling.Backend.Dtos;

/// <summary>One filter row of a browse request. <c>Logic</c> names the operator joining this row to the previous one: first, and, or (case-insensitive).</summary>
public sealed record BrowseFilterDto(
    string? Column,
    string? Op,
    string? Value,
    bool Enabled = true,
    string? Logic = "first");

/// <summary>One sort key of a browse request. <c>Order</c> is asc or desc (case-insensitive).</summary>
public sealed record BrowseSortDto(
    string? Column,
    string? Order = "asc");

/// <summary>A structured paged read of one table: rows + the filtered total in one response.</summary>
public sealed record BrowseRequest(
    string? Table,
    string? Database = null,
    int Page = 1,
    int Limit = 100,
    IReadOnlyList<BrowseFilterDto>? Filters = null,
    IReadOnlyList<BrowseSortDto>? Sort = null,
    string? ManualCondition = null,
    bool Refresh = false);

/// <summary>
/// The browse payload: one page of rows plus the total matching the filter.
/// <c>CacheTtlSeconds</c> is the time left before the snapshot stops being
/// served; it is absent when this answer holds no snapshot at all.
/// </summary>
public sealed record BrowseResponse(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    long Total,
    bool FromCache,
    DateTimeOffset? CachedAt,
    long? CacheTtlSeconds = null);
