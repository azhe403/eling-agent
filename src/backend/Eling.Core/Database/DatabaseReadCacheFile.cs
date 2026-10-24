namespace Eling.Core.Database;

/// <summary>
/// The on-disk shape of the read cache. One file holds every connection's
/// entries, so the whole cache is swapped atomically on write.
/// </summary>
public sealed record DatabaseReadCacheFile(
    List<CachedSchemaEntry> Schemas,
    List<CachedQueryEntry> Queries,
    List<CachedDatabaseListEntry> DatabaseLists);