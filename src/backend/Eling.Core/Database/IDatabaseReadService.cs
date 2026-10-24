namespace Eling.Core.Database;

/// <summary>
/// The single read path for database reads. Both the HTTP endpoints and the MCP
/// tools go through this service, so the cached fallback behaves identically on
/// both surfaces.
/// </summary>
public interface IDatabaseReadService
{
    Task<QueryResult> QueryAsync(
        DatabaseConnection connection,
        string? database,
        string sql,
        int maxRows,
        CancellationToken cancellationToken = default);

    Task<DatabaseSchemaInfo> GetSchemaAsync(
        DatabaseConnection connection,
        string? database,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListDatabasesAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken = default);
}