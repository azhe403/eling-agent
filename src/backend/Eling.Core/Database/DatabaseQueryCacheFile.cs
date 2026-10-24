using System.Text.Json;

namespace Eling.Core.Database;

/// <summary>
/// The on-disk shape of the query (data) cache. Kept in its own file so the
/// (frequently changing) data file is small and cheap to rewrite.
/// </summary>
public sealed record DatabaseQueryCacheFile(
    List<CachedQueryEntry> Queries);