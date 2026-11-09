namespace Eling.Core.Database;

public sealed record RedisPrefixPreviewResult(
    string Prefix,
    IReadOnlyList<string> Keys,
    long Total,
    bool Truncated);
