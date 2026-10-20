namespace Eling.Backend.Dtos;

/// <summary>
/// Memory counts for the dashboard's vitals row: how many memories there are
/// and how they break down by type, status and scope.
/// </summary>
/// <remarks>
/// A count-only view on purpose. The dashboard's first tile is a handful of
/// numbers, and the way to get them is to send every memory to the browser
/// and count it there - the corpus grows with every save, so that payload
/// grows with it. The backend already holds the same list in memory to serve
/// the aggregated list, so counting it there costs one pass and returns a
/// few hundred bytes.
/// <para>
/// The scope breakdown uses the same wire spellings as the rest of the API
/// (<c>global</c>, <c>project</c>, <c>project-local</c>), so the dashboard can
/// label a scope without a second translation table.
/// </para>
/// </remarks>
/// <param name="Total">Every memory the aggregation covers.</param>
/// <param name="ByType">Count per memory type - Fact, Preference, Decision, Lesson, Note.</param>
/// <param name="ByStatus">Count per status - Active, Superseded, Archived.</param>
/// <param name="ByScope">Count per memory scope.</param>
public sealed record MemoryStatsDto(
    int Total,
    IReadOnlyDictionary<string, int> ByType,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> ByScope);
