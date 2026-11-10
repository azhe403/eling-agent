namespace Eling.Backend.Dtos;

/// <summary>
/// The answer to a key recheck: whether it is still there, and its current TTL.
///
/// The dashboard asks this when a key's countdown reaches zero, so the row can
/// say "gone" or take the new expiry. Both fields are what the row needs and
/// nothing else - no value, no preview.
/// </summary>
public sealed record RedisKeyProbeDto(
    string Key,
    bool Exists,
    long TtlSeconds,
    DateTimeOffset? ExpiresAt);
