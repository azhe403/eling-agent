namespace Eling.Core.Database;

public sealed record RedisClassification(
    RedisVerdict Verdict,
    string Command,
    string? Reason);