namespace Eling.Core.Database;

/// <summary>
/// The two refusals a provider mismatch produces.
///
/// <para>
/// A Redis connection and a SQL connection answer different questions, so a
/// route or tool aimed at one has to refuse the other. That refusal was written
/// out at every site - seven times, in three different wordings - which meant a
/// person who hit it got a different sentence depending on which route they
/// used. The wording lives here now; each caller only names the operation it
/// wanted.
/// </para>
/// </summary>
public static class RedisRequirement
{
    /// <summary>
    /// The refusal for a Redis-only operation that met a connection of another
    /// provider.
    /// </summary>
    /// <param name="connection">The connection that was asked.</param>
    /// <param name="operation">What the caller wanted, e.g. "key browsing".</param>
    public static string NotRedis(DatabaseConnection connection, string operation) =>
        $"connection '{connection.Id}' is a {connection.Provider} connection; {operation} requires Redis";

    /// <summary>
    /// The refusal for a SQL-only operation that met a Redis connection.
    /// </summary>
    /// <param name="connection">The connection that was asked.</param>
    /// <param name="advice">What the caller should use instead, e.g. "use /keys".</param>
    public static string NotSql(DatabaseConnection connection, string advice) =>
        $"connection '{connection.Id}' is a Redis key-value store; {advice}";

    /// <summary>
    /// The refusal when a standalone-only operation met a cluster or sentinel
    /// deployment. The key tree reads one node's keyspace, so the answer would
    /// be silently incomplete; the message says that and says what to do.
    /// </summary>
    public static readonly string StandaloneOnly =
        "Redis Cluster is not supported in this phase: key scanning only sees one node's slots, so the tree would silently be incomplete. Register a standalone Redis instance instead.";
}
