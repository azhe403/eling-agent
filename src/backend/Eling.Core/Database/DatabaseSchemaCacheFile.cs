using System.Text.Json;

namespace Eling.Core.Database;

/// <summary>
/// The on-disk shape of the schema cache (tables, columns, FKs, indexes, and
/// the database list for server-level connections). Kept in its own file so a
/// data write never rewrites the (rarely changing) schema file.
/// </summary>
public sealed record DatabaseSchemaCacheFile(
    List<CachedSchemaEntry> Schemas,
    List<CachedDatabaseListEntry> DatabaseLists);