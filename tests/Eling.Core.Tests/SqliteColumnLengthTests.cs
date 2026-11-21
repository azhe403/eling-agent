using Eling.Core.Database;
using Eling.Core.Database.Adapters;

namespace Eling.Core.Tests;

/// <summary>
/// A character column reports the length it was declared with, so the schema
/// view can say "varchar(50)" instead of a bare "varchar". SQLite is the one
/// provider that keeps the whole declared type as a string, so its adapter is
/// where the length has to be read back out of the text - and where the types
/// that carry no length must not invent one.
/// </summary>
public sealed class SqliteColumnLengthTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"eling-col-len-{Ulid.NewUlid()}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private SqliteDatabaseAdapter Adapter() => new(
        DatabaseConnection.Create(
            "col-len",
            DatabaseProvider.Sqlite,
            $"Data Source={_dbPath}",
            DatabaseEnvironment.ReadOnly));

    private static async Task ExecuteAsync(SqliteDatabaseAdapter adapter, string sql) =>
        await adapter.ExecuteAsync(sql);

    [Fact]
    public async Task GetSchemaAsync_ReportsDeclaredLength_ForVarcharAndChar()
    {
        var adapter = Adapter();
        await ExecuteAsync(adapter, "CREATE TABLE mst_driver (" +
            "driver_id VARCHAR(50) NOT NULL PRIMARY KEY, " +
            "pool_code CHAR(4), " +
            "notes TEXT, " +
            "amount DECIMAL(10,2));");

        var schema = await adapter.GetSchemaAsync();
        var columns = schema.Tables.Single(t => t.Name == "mst_driver").Columns;

        Assert.Equal(50, columns.Single(c => c.Name == "driver_id").MaxLength);
        Assert.Equal(4, columns.Single(c => c.Name == "pool_code").MaxLength);
        // TEXT declares no length, and a decimal's numbers are precision and
        // scale - not a string length to render as "decimal(10)".
        Assert.Null(columns.Single(c => c.Name == "notes").MaxLength);
        Assert.Null(columns.Single(c => c.Name == "amount").MaxLength);
    }

    [Fact]
    public async Task GetSchemaAsync_LeavesMaxLengthNull_WhenTypeDeclaresNone()
    {
        var adapter = Adapter();
        await ExecuteAsync(adapter, "CREATE TABLE t (code VARCHAR, name TEXT, n INTEGER);");

        var columns = (await adapter.GetSchemaAsync()).Tables.Single(t => t.Name == "t").Columns;

        Assert.All(columns, c => Assert.Null(c.MaxLength));
    }
}
