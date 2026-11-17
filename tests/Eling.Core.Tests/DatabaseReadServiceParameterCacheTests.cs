using Eling.Core.Database;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Core.Tests;

/// <summary>
/// A cached entry must answer for the parameter set that wrote it and no
/// other - the same placeholder text with two filter values is two different
/// queries, and must be two different cache entries.
/// </summary>
public sealed class DatabaseReadServiceParameterCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eling-read-service-param-cache-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A MySQL connection to a refused port: opening it fails fast.</summary>
    private static DatabaseConnection BrokenConnection()
        => DatabaseConnection.Create(
            "broken@local",
            DatabaseProvider.MySql,
            "Server=127.0.0.1;Port=1;Database=broken;User Id=u;Password=p;ConnectionTimeout=1",
            DatabaseEnvironment.ReadOnly);

    [Fact]
    public async Task QueryAsync_ParametersInCacheKey_ServeTheMatchingSnapshot()
    {
        var connection = BrokenConnection();
        var cache = new JsonDatabaseReadCache(_dir);
        var service = new DatabaseReadService(cache, NullLogger<DatabaseReadService>.Instance);

        const string sql = "SELECT * FROM \"mst_test\" WHERE \"name\" = @p0 LIMIT 10 OFFSET 0";
        var p1 = (IReadOnlyList<SqlParam>)[new SqlParam("@p0", "a")];
        var p2 = (IReadOnlyList<SqlParam>)[new SqlParam("@p0", "b")];

        var resultA = new QueryResult(["id"], [[1]], false);
        var resultB = new QueryResult(["id"], [[2]], false);

        cache.SetQuery(connection.Id, null, 10, QueryCacheKey.For(sql, p1), resultA);
        cache.SetQuery(connection.Id, null, 10, QueryCacheKey.For(sql, p2), resultB);

        var answerA = await service.QueryAsync(connection, null, sql, 10, preferCache: true, parameters: p1);
        var answerB = await service.QueryAsync(connection, null, sql, 10, preferCache: true, parameters: p2);

        Assert.Equal("1", answerA.Rows[0][0]?.ToString());
        Assert.Equal("2", answerB.Rows[0][0]?.ToString());
        Assert.True(answerA.FromCache);
        Assert.True(answerB.FromCache);
    }
}
