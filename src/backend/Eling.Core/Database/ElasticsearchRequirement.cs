namespace Eling.Core.Database;

/// <summary>
/// The two refusals an Elasticsearch connection answers to.
///
/// <para>
/// An Elasticsearch cluster is read-only in this product and belongs to no SQL
/// family, so a route or tool aimed at one has to refuse the other. Mirrors
/// <see cref="RedisRequirement"/>: the wording lives here, and each caller only
/// names the operation it wanted.
/// </para>
/// </summary>
public static class ElasticsearchRequirement
{
    /// <summary>
    /// The refusal for an Elasticsearch-only operation that met a connection of
    /// another provider.
    /// </summary>
    /// <param name="connection">The connection that was asked.</param>
    /// <param name="operation">What the caller wanted, e.g. "index browsing".</param>
    public static string NotElasticsearch(DatabaseConnection connection, string operation) =>
        $"connection '{connection.Id}' is a {connection.Provider} connection; {operation} requires Elasticsearch";

    /// <summary>
    /// The refusal for a SQL-only operation that met an Elasticsearch cluster.
    /// </summary>
    /// <param name="connection">The connection that was asked.</param>
    /// <param name="advice">What the caller should use instead, e.g. "use es_mapping / es_search".</param>
    public static string NotSql(DatabaseConnection connection, string advice) =>
        $"connection '{connection.Id}' is an Elasticsearch cluster; {advice}";

    /// <summary>
    /// The refusal when a cluster answers the ping with an ES 7 version. The
    /// connection test surfaces this verbatim; the fix is an ES 8 cluster, not a
    /// different client setting.
    /// </summary>
    public static readonly string UnsupportedVersion =
        "ES 7 is not supported; use an ES 8 cluster";
}
