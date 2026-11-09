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

    public const string DetectConnections = "db_detect_connections";

    public const string Keys = "db_keys";

    public const string InspectKey = "db_inspect_key";

    public const string RedisCommand = "redis_command";

    public const string RedisDatabases = "redis_databases";

    public const string RedisDelete = "redis_delete";

    public const string RedisPrefixDelete = "redis_prefix_delete";
}
