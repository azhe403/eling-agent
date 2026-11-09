namespace Eling.Backend.Dtos;

/// <summary>
/// The answer to deleting one prefix subtree: how many keys actually went, and
/// whether the scan budget stopped it early (a truncated run is never a full
/// one).
/// </summary>
public sealed record RedisPrefixDeleteDto(
    long Deleted,
    bool Truncated);
