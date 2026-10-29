namespace Eling.Core.Database;

/// <summary>
/// The read-only Redis surface: browse a connection as a lazy key tree, inspect
/// one key, and run a whitelisted command. Nothing here writes.
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
}
