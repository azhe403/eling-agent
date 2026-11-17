namespace Eling.Core.Database.Browse;

/// <summary>
/// The wire and internal shape of one filter row of the browse condition
/// builder. Values are raw strings from the request; coercion against the
/// declared column type happens inside <see cref="BrowseQueryBuilder"/>, not
/// here. <paramref name="Logic"/> is the operator joining this row to the one
/// before it; on the first enabled row it is ignored.
/// </summary>
public sealed record BrowseFilter(
    string Column,
    string Op,
    string? Value,
    bool Enabled,
    string Logic);

/// <summary>One sort key. <paramref name="Order"/> is <c>asc</c> or <c>desc</c> (case-insensitive).</summary>
public sealed record BrowseSort(string Column, string Order);

/// <summary>A SQL fragment and the parameters that travel with it.</summary>
public sealed record BrowseQuery(string Sql, IReadOnlyList<SqlParam> Parameters);

/// <summary>
/// The pair of statements one browse request needs, plus a readable
/// description of the fold for the audit log. <see cref="WhereText"/> has
/// values inlined as SQL literals; it is truncated to 600 characters and is
/// never used to execute anything.
/// </summary>
public sealed record BrowsePlan(BrowseQuery Rows, BrowseQuery Count, string WhereText);
