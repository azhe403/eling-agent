namespace Eling.Core.Database;

/// <summary>
/// Canonical action names recorded on the database access trail, spelled as the
/// MCP tool names so filtering the trail reads back as "this tool ran" with no
/// translation table (matches the codebase/memory domain convention). The
/// mutate action keeps its existing "db.mutate" spelling for backwards
/// compatibility with already-written change entries.
/// </summary>
public static class DatabaseAuditActions
{
    public const string ListConnections = "db_list_connections";

    public const string GetConnection = "db_get_connection";

    public const string GetSchema = "db_get_schema";

    public const string Query = "db_query";

    /// <summary>
    /// The structured browse read (Data tab, filter &amp; sort builder). Mirrors
    /// the <c>db.mutate</c> spelling style: a dotted action rather than an MCP
    /// tool name, because <c>/api/databases/{id}/browse</c> has no MCP twin.
    /// </summary>
    public const string Browse = "db.browse";

    public const string DetectConnections = "db_detect_connections";

    public const string Keys = "db_keys";

    public const string InspectKey = "db_inspect_key";

    public const string RedisCommand = "redis_command";

    public const string RedisDatabases = "redis_databases";

    public const string RedisDelete = "redis_delete";

    public const string RedisPrefixPreview = "redis_prefix_preview";

    public const string RedisPrefixDelete = "redis_prefix_delete";

    public const string EsIndices = "es_indices";

    public const string EsMapping = "es_mapping";

    public const string EsSearch = "es_search";

    public const string EsCount = "es_count";
}
