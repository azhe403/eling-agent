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
        CancellationToken cancellationToken = default,
        bool preferCache = true);

    Task<KeyInspectResult> InspectAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        string key,
        CancellationToken cancellationToken = default,
        bool preferCache = true);

    /// <summary>
    /// Whether a key is still there, and its TTL, without reading its value.
    ///
    /// The dashboard asks this once when a key's countdown reaches zero: the
    /// row it is showing was a claim about the future, and the answer decides
    /// whether the key is really gone or has a new expiry.
    /// </summary>
    Task<RedisKeyProbeResult> ProbeKeyAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same question for many keys at once, answered in one round trip.
    ///
    /// A database with short-lived keys expires them in bunches - a batch job
    /// finishing, a queue draining - and one request per key would turn that
    /// moment into a burst of round trips that all say the same thing. EXISTS
    /// and TTL take many keys in Redis, so the burst is one call per side.
    ///
    /// <para>
    /// A key missing from the answer is a key that is gone: the caller asked
    /// about keys it believes exist, so `Exists` is false there too. Results
    /// keep the order of <paramref name="keys"/>.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<RedisKeyProbeResult>> ProbeKeysAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default);

    Task<RedisCommandResult> ExecuteCommandAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        string command,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RedisDatabaseInfo>> ListDatabasesAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken = default,
        bool preferCache = true);

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
