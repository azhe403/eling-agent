using Eling.Core.Database;
using Eling.Core.Database.Adapters;

namespace Eling.Core.Tests;

public sealed class DatabaseAdapterFactoryTests
{
    [Fact]
    public void CreateAdapter_PostgreSql_ReturnsPostgreSqlDatabaseAdapter()
    {
        var connection = DatabaseConnection.Create(
            "pg",
            DatabaseProvider.PostgreSql,
            "Host=localhost;Database=orders;",
            DatabaseEnvironment.ReadOnly);

        Assert.IsType<PostgreSqlDatabaseAdapter>(DatabaseAdapterFactory.CreateAdapter(connection));
    }
}
