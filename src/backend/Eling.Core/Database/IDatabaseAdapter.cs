namespace Eling.Core.Database;

public interface IDatabaseAdapter
{
    Task<ConnectionTestResult> TestAsync(CancellationToken ct = default);
    Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default);
    Task<int> ExecuteAsync(string sql, CancellationToken ct = default);

    /// <summary>
    /// The schema of the connection. With no <paramref name="database"/> it is
    /// the schema of the connection's own default database; with one, it is
    /// the schema of that database - which is how a server-level connection
    /// (one without a default database) reaches a single database's tables.
    /// </summary>
    Task<DatabaseSchemaInfo> GetSchemaAsync(string? database = null, CancellationToken ct = default);

    /// <summary>
    /// The databases on the server this connection points at. A provider that
    /// has no such concept returns an empty list.
    /// </summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default);
}
