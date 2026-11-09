namespace Eling.Backend.Dtos;

/// <summary>
/// The answer to deleting one Redis key from the dashboard: the key is gone.
/// </summary>
public sealed record RedisKeyDeleteDto(
    bool Deleted);
