using System;

namespace Eling.Core.Database;

public interface IDatabaseConnectionStore
{
    Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken ct = default);
    Task<DatabaseConnection?> FindAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(DatabaseConnection connection, CancellationToken ct = default);
    Task<bool> RemoveAsync(string id, CancellationToken ct = default);
    Task MarkConnectedAsync(string id, DateTimeOffset at, CancellationToken ct = default);
}
