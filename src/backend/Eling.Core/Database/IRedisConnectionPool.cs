using StackExchange.Redis;

namespace Eling.Core.Database;

public interface IRedisConnectionPool
{
    Task<IConnectionMultiplexer> GetAsync(DatabaseConnection connection, CancellationToken ct = default);
    void Invalidate(string connectionId);
    ValueTask DisposeAsync();
}