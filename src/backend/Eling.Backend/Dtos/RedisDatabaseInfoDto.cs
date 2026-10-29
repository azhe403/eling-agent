namespace Eling.Backend.Dtos;

/// <summary>
/// One logical Redis database as the dashboard sees it: the index and how many
/// keys it holds. The count is what the tree row shows next to the name.
///
/// <para>
/// <paramref name="FromCache"/> is true when the server could not be reached
/// and the answer came from the read cache; <paramref name="CachedAt"/> then
/// says how old it is, and the dashboard shows that as "as of".
/// </para>
/// </summary>
public sealed record RedisDatabaseInfoDto(
    int Index,
    long Keys,
    bool FromCache = false,
    DateTimeOffset? CachedAt = null);
