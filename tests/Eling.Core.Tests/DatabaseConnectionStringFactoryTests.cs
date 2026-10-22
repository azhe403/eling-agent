using Eling.Core.Database;

namespace Eling.Core.Tests;

public sealed class DatabaseConnectionStringFactoryTests
{
    [Fact]
    public void Build_MySql_WritesEveryField()
    {
        var fields = new ManualConnectionFields(
            Host: "10.0.0.5",
            Port: 3307,
            Database: "orders",
            Username: "app",
            Password: "secret",
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal("10.0.0.5", parsed.Host);
        Assert.Equal(3307, parsed.Port);
        Assert.Equal("orders", parsed.Database);
        Assert.Equal("app", parsed.Username);
        Assert.Equal("secret", parsed.Password);
    }

    [Fact]
    public void Build_MySql_WithSeparatorsInPassword_RoundTripsThePassword()
    {
        var password = "p@ss;word=1'x";
        var fields = new ManualConnectionFields(
            Host: "db.internal",
            Port: 3306,
            Database: "orders",
            Username: "app",
            Password: password,
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal(password, parsed.Password);
    }

    [Fact]
    public void Build_MySql_WithoutPort_UsesTheDefaultPort()
    {
        var fields = new ManualConnectionFields(
            Host: "10.0.0.5",
            Port: null,
            Database: "orders",
            Username: null,
            Password: null,
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal(DatabaseConnectionStringFactory.DefaultMySqlPort, parsed.Port);
    }

    [Fact]
    public void Build_MySql_WithoutHost_Throws()
    {
        var fields = new ManualConnectionFields(
            Host: "  ",
            Port: 3306,
            Database: "orders",
            Username: null,
            Password: null,
            FilePath: null);

        var act = () => DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql, fields);

        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void Build_MySql_WithoutDatabase_BuildsAServerLevelConnection()
    {
        var fields = new ManualConnectionFields(
            Host: "10.0.0.5",
            Port: 3306,
            Database: null,
            Username: "app",
            Password: "secret",
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal("10.0.0.5", parsed.Host);
        Assert.Null(parsed.Database);
        Assert.True(DatabaseConnectionStringFactory.IsServerLevel(DatabaseProvider.MySql, connectionString));
    }

    [Fact]
    public void Build_Sqlite_WritesTheDataSource()
    {
        var fields = new ManualConnectionFields(
            Host: null,
            Port: null,
            Database: null,
            Username: null,
            Password: null,
            FilePath: "C:\\data\\eling-index.db");

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Sqlite, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Sqlite, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal("C:\\data\\eling-index.db", parsed.FilePath);
    }

    [Fact]
    public void Build_Sqlite_WithoutFilePath_Throws()
    {
        var fields = new ManualConnectionFields(
            Host: null,
            Port: null,
            Database: null,
            Username: null,
            Password: null,
            FilePath: " ");

        var act = () => DatabaseConnectionStringFactory.Build(DatabaseProvider.Sqlite, fields);

        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void Parse_StringThatIsNotAConnectionString_ReturnsNull()
    {
        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, "this is not a connection string");

        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_BlankString_ReturnsNull()
    {
        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, "   ");

        Assert.Null(parsed);
    }

    [Fact]
    public void IsServerLevel_AConnectionWithADatabase_IsFalse()
    {
        var fields = new ManualConnectionFields(
            Host: "10.0.0.5",
            Port: 3306,
            Database: "orders",
            Username: null,
            Password: null,
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql, fields);

        Assert.False(DatabaseConnectionStringFactory.IsServerLevel(DatabaseProvider.MySql, connectionString));
    }

    [Fact]
    public void IsServerLevel_ASqliteFile_IsFalse()
    {
        var fields = new ManualConnectionFields(
            Host: null,
            Port: null,
            Database: null,
            Username: null,
            Password: null,
            FilePath: "C:\\data\\eling-index.db");

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Sqlite, fields);

        Assert.False(DatabaseConnectionStringFactory.IsServerLevel(DatabaseProvider.Sqlite, connectionString));
    }

    [Fact]
    public void Update_BlankPassword_KeepsTheStoredPassword()
    {
        var stored = DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql,
            new ManualConnectionFields("10.0.0.5", 3306, "orders", "app", "secret", null));
        var entered = new ManualConnectionFields(
            Host: "10.0.0.5",
            Port: 3307,
            Database: "orders",
            Username: "app",
            Password: null,
            FilePath: null);

        var updated = DatabaseConnectionStringFactory.Update(DatabaseProvider.MySql, stored, entered);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, updated);
        Assert.NotNull(parsed);
        Assert.Equal(3307, parsed.Port);
        Assert.Equal("secret", parsed.Password);
        Assert.Equal("app", parsed.Username);
        Assert.Equal("orders", parsed.Database);
    }

    [Fact]
    public void Update_EnteredPassword_ReplacesTheStoredPassword()
    {
        var stored = DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql,
            new ManualConnectionFields("10.0.0.5", 3306, "orders", "app", "old", null));
        var entered = new ManualConnectionFields(
            Host: null,
            Port: null,
            Database: null,
            Username: null,
            Password: "new",
            FilePath: null);

        var updated = DatabaseConnectionStringFactory.Update(DatabaseProvider.MySql, stored, entered);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, updated);
        Assert.NotNull(parsed);
        Assert.Equal("new", parsed.Password);
        Assert.Equal("10.0.0.5", parsed.Host);
        Assert.Equal(3306, parsed.Port);
    }

    [Fact]
    public void Update_BlankDatabase_ClearsTheStoredDatabase()
    {
        var stored = DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql,
            new ManualConnectionFields("10.0.0.5", 3306, "orders", "app", "secret", null));
        var entered = new ManualConnectionFields(
            Host: "10.0.0.5",
            Port: 3306,
            Database: null,
            Username: "app",
            Password: null,
            FilePath: null);

        var updated = DatabaseConnectionStringFactory.Update(DatabaseProvider.MySql, stored, entered);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, updated);
        Assert.NotNull(parsed);
        Assert.Null(parsed.Database);
        Assert.True(DatabaseConnectionStringFactory.IsServerLevel(DatabaseProvider.MySql, updated));
    }

    [Fact]
    public void Update_KeepsTheOptionsNoFieldCovers()
    {
        // A raw connection string can carry options the form has no field
        // for - an SSL mode here. Editing it must not drop them. The builder
        // rewrites the key as "SSL Mode", so the option is checked by value.
        var stored = "Server=10.0.0.5;Port=3306;Database=orders;User ID=app;Password=secret;SslMode=Required";
        var entered = new ManualConnectionFields(
            Host: "10.0.0.5",
            Port: 3307,
            Database: "orders",
            Username: "app",
            Password: null,
            FilePath: null);

        var updated = DatabaseConnectionStringFactory.Update(DatabaseProvider.MySql, stored, entered);

        var builder = new MySqlConnector.MySqlConnectionStringBuilder(updated);
        Assert.Equal(MySqlConnector.MySqlSslMode.Required, builder.SslMode);
        Assert.Equal(3307u, builder.Port);
    }

    [Fact]
    public void WithDatabase_PointsTheConnectionAtThatDatabase()
    {
        var stored = DatabaseConnectionStringFactory.Build(DatabaseProvider.MySql,
            new ManualConnectionFields("10.0.0.5", 3306, null, "app", "secret", null));

        var updated = DatabaseConnectionStringFactory.WithDatabase(DatabaseProvider.MySql, stored, "orders");

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.MySql, updated);
        Assert.NotNull(parsed);
        Assert.Equal("orders", parsed.Database);
        Assert.Equal("secret", parsed.Password);
        Assert.False(DatabaseConnectionStringFactory.IsServerLevel(DatabaseProvider.MySql, updated));
    }
}
