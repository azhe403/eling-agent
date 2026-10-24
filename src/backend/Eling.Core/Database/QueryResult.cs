namespace Eling.Core.Database;

public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated)
{
    public bool FromCache { get; init; }
    public DateTimeOffset? CachedAt { get; init; }
}
