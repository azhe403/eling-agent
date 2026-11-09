namespace Eling.Core.Database;

/// <summary>
/// The Redis surface: browse a connection as a lazy key tree, inspect one key,
/// run a whitelisted command, and — for the dashboard only — delete one key or a
/// whole prefix subtree. The deletes are human-initiated writes from the
/// Databases page; the command console and the agent surface stay read-only.
/// </summary>
public interface IRedisKeyReadService
{
    Task<KeyPage> ScanAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        string prefix,
        long cursor,
        int pageOffset,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<KeyInspectResult> InspectAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        string key,
        CancellationToken cancellationToken = default);

    Task<RedisCommandResult> ExecuteCommandAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        string command,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RedisDatabaseInfo>> ListDatabasesAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken = default);

    Task<RedisKeyDeleteResult> DeleteKeyAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        string key,
        CancellationToken ct = default);

    Task<RedisPrefixPreviewResult> PreviewPrefixAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        string prefix,
        CancellationToken ct = default);

    Task<RedisPrefixDeleteResult> DeletePrefixAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        string prefix,
        CancellationToken ct = default);
}
