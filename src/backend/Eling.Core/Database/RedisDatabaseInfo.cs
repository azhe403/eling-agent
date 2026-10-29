namespace Eling.Core.Database;

public sealed record RedisDatabaseInfo(
    int Index,
    long Keys,
    bool FromCache = false,
    DateTimeOffset? CachedAt = null);