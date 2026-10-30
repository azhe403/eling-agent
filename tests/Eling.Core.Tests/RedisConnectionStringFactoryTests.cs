using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The Redis side of the connection-string round trip: a person types
/// host, port, user, password, and an optional logical database, and the
/// factory builds a string StackExchange.Redis can parse back into those
/// same fields. The two ways that break are a password the builder cannot
/// quote and a database index outside 0-15, so both are pinned here.
/// </summary>
public sealed class RedisConnectionStringFactoryTests
{
    [Fact]
    public void Build_Redis_WritesEveryField()
    {
        var fields = new ManualConnectionFields(
            Host: "cache.internal",
            Port: 6380,
            Database: "3",
            Username: "app",
            Password: "secret",
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Redis, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal("cache.internal", parsed.Host);
        Assert.Equal(6380, parsed.Port);
        Assert.Equal("3", parsed.Database);
        Assert.Equal("app", parsed.Username);
        Assert.Equal("secret", parsed.Password);
    }

    [Fact]
    public void Build_Redis_PasswordWithSeparators_RoundTrips()
    {
        // The round trip holds for the separators the Redis connection string
        // grammar keeps apart: ';' and '='. A comma inside a password cannot
        // round-trip - ConfigurationOptions.ToString() writes the value
        // unquoted, and the parser cuts it at the next comma - so the test
        // does not promise what the builder cannot keep.
        var password = "p@ss;word=1";
        var fields = new ManualConnectionFields(
            Host: "cache.internal",
            Port: 6379,
            Database: null,
            Username: null,
            Password: password,
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Redis, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal(password, parsed.Password);
    }

    [Fact]
    public void Build_Redis_PasswordWithAComma_IsRefused()
    {
        var fields = new ManualConnectionFields(
            Host: "cache.internal",
            Port: 6379,
            Database: null,
            Username: null,
            Password: "p@ss,word",
            FilePath: null);

        Assert.Throws<ArgumentException>(() => DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis, fields));
    }

    [Fact]
    public void Build_Redis_WithoutPort_UsesTheDefaultPort()
    {
        var fields = new ManualConnectionFields(
            Host: "cache.internal",
            Port: null,
            Database: null,
            Username: null,
            Password: null,
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Redis, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal(DatabaseConnectionStringFactory.DefaultRedisPort, parsed.Port);
    }

    [Fact]
    public void Build_Redis_WithoutHost_Throws()
    {
        var fields = new ManualConnectionFields(
            Host: "  ",
            Port: 6379,
            Database: null,
            Username: null,
            Password: null,
            FilePath: null);

        Assert.Throws<ArgumentException>(() => DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis, fields));
    }

    [Theory]
    [InlineData("16")]
    [InlineData("-1")]
    [InlineData("99")]
    [InlineData("abc")]
    public void Build_Redis_DatabaseOutsideTheRange_Throws(string database)
    {
        var fields = new ManualConnectionFields(
            Host: "cache.internal",
            Port: 6379,
            Database: database,
            Username: null,
            Password: null,
            FilePath: null);

        Assert.Throws<ArgumentException>(() => DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis, fields));
    }

    [Fact]
    public void Parse_Redis_GibberishTreatedAsAnEndpoint_DoesNotThrow()
    {
        // The options builder is permissive: arbitrary text becomes an
        // endpoint instead of a parse failure. That is a library fact, not
        // a contract the factory promises - so a nonsensical string reads
        // back with the text as its host rather than as null.
        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Redis, "totally-unreadable-gibberish");

        Assert.NotNull(parsed);
        Assert.Equal("totally-unreadable-gibberish", parsed.Host);
    }

    [Fact]
    public void Parse_Redis_AStoredString_ReadsTheFieldsBack()
    {
        var fields = new ManualConnectionFields(
            Host: "cache.internal",
            Port: 6380,
            Database: "5",
            Username: "app",
            Password: "secret",
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Redis, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal("cache.internal", parsed.Host);
        Assert.Equal(6380, parsed.Port);
        Assert.Equal("5", parsed.Database);
        Assert.Equal("app", parsed.Username);
        Assert.Equal("secret", parsed.Password);
    }

    [Fact]
    public void WithDatabase_Redis_PointsTheConnectionAtThatDatabase()
    {
        var stored = DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis,
            new ManualConnectionFields("cache.internal", 6379, null, "app", "secret", null));

        var updated = DatabaseConnectionStringFactory.WithDatabase(DatabaseProvider.Redis, stored, "7");

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Redis, updated);
        Assert.NotNull(parsed);
        Assert.Equal("7", parsed.Database);
        Assert.Equal("cache.internal", parsed.Host);
        Assert.Equal("secret", parsed.Password);
    }

    [Fact]
    public void Update_Redis_BlankPassword_KeepsTheStoredPassword()
    {
        var stored = DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis,
            new ManualConnectionFields("cache.internal", 6379, null, "app", "secret", null));
        var entered = new ManualConnectionFields(
            Host: "cache.internal",
            Port: 6380,
            Database: null,
            Username: "app",
            Password: null,
            FilePath: null);

        var updated = DatabaseConnectionStringFactory.Update(DatabaseProvider.Redis, stored, entered);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Redis, updated);
        Assert.NotNull(parsed);
        Assert.Equal(6380, parsed.Port);
        Assert.Equal("secret", parsed.Password);
        Assert.Equal("app", parsed.Username);
    }

    [Fact]
    public void Update_Redis_EnteredPassword_ReplacesTheStoredPassword()
    {
        var stored = DatabaseConnectionStringFactory.Build(DatabaseProvider.Redis,
            new ManualConnectionFields("cache.internal", 6379, null, "app", "old", null));
        var entered = new ManualConnectionFields(
            Host: null,
            Port: null,
            Database: null,
            Username: null,
            Password: "new",
            FilePath: null);

        var updated = DatabaseConnectionStringFactory.Update(DatabaseProvider.Redis, stored, entered);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Redis, updated);
        Assert.NotNull(parsed);
        Assert.Equal("new", parsed.Password);
        Assert.Equal("cache.internal", parsed.Host);
    }
}
