namespace Eling.Core.Database;

/// <summary>
/// One bound parameter for a SQL statement. Adapters map this to their own
/// parameter type; the read cache folds it into the query key when present.
/// Names are unique per statement and spelled with a leading <c>@</c>.
/// </summary>
public sealed record SqlParam(string Name, object? Value);
