namespace Eling.Core.Database;

public sealed record RedisPrefixDeleteResult(
    long Deleted,
    bool Truncated);
