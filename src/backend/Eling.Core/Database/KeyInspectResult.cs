namespace Eling.Core.Database;

public sealed record KeyInspectResult(
    string Key,
    string Type,
    long TtlSeconds,
    DateTimeOffset? ExpiresAt,
    long Length,
    string? Encoding,
    KeyInspectPreview Preview,
    bool FromCache = false,
    DateTimeOffset? CachedAt = null);