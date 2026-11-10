namespace Eling.Core.Database;

/// <summary>
/// What a probe found for one key: whether it is still there, and its current
/// TTL.
///
/// <para>
/// This is the cheap read behind the dashboard's expiry recheck. A key whose
/// countdown reached zero has either died or been rewritten with a new expiry
/// (a writer may have touched it), and only the server can say which. It is
/// deliberately not <see cref="KeyInspectResult"/>: that one fetches the value
/// and builds a preview, which is far more than a "does it still exist" needs
/// and would run once per expiring key.
/// </para>
/// </summary>
public sealed record RedisKeyProbeResult(
    string Key,
    bool Exists,
    long TtlSeconds,
    DateTimeOffset? ExpiresAt);
