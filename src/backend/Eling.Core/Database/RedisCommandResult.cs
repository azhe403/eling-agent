namespace Eling.Core.Database;

public sealed record RedisCommandResult(
    string Kind,
    object? Value);