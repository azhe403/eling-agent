namespace Eling.Core.Database;

public interface IDatabaseAdapter
{
    Task<ConnectionTestResult> TestAsync(CancellationToken ct = default);
    Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default);
    Task<DatabaseSchemaInfo> GetSchemaAsync(CancellationToken ct = default);
}
