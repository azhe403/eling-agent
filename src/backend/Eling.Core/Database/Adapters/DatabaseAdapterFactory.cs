using Eling.Core.Database;

namespace Eling.Core.Database.Adapters;

public static class DatabaseAdapterFactory
{
    public static IDatabaseAdapter CreateAdapter(DatabaseConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Provider switch
        {
            DatabaseProvider.Sqlite => new SqliteDatabaseAdapter(connection),
            DatabaseProvider.MySql => new MySqlDatabaseAdapter(connection),
            _ => throw new NotSupportedException($"Provider '{connection.Provider}' is not supported.")
        };
    }
}
