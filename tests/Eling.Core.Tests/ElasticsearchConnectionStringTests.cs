using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The Elasticsearch side of the connection-string round trip: a person types
/// host, port and optional basic-auth credentials, and the factory builds a
/// URI Elasticsearch's own HTTP stack can parse back into those same fields.
/// Unlike the SQL providers, the string is a URI, so the two ways to break it
/// are a userinfo component containing URI separators - percent-encoded on
/// build, decoded on parse - and a stored string that is not a URI at all,
/// which reads back as null exactly like an unreadable SQL string.
/// </summary>
public sealed class ElasticsearchConnectionStringTests
{
    [Fact]
    public void Build_NoAuth_RendersHostAndDefaultPort()
    {
        var fields = new ManualConnectionFields(
            Host: "127.0.0.1",
            Port: null,
            Database: null,
            Username: null,
            Password: null,
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Elasticsearch, fields);

        Assert.Equal("http://127.0.0.1:9200/", connectionString);
    }

    [Fact]
    public void Build_WithCredentials_RendersUserInfo()
    {
        var fields = new ManualConnectionFields(
            Host: "127.0.0.1",
            Port: 9200,
            Database: null,
            Username: "u",
            Password: "p",
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Elasticsearch, fields);

        Assert.Equal("http://u:p@127.0.0.1:9200/", connectionString);
    }

    [Fact]
    public void Build_EncodesPasswordSpecials()
    {
        var password = "pa:ss@wo:rd=a&b";
        var fields = new ManualConnectionFields(
            Host: "es.internal",
            Port: 9200,
            Database: null,
            Username: "u",
            Password: password,
            FilePath: null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Elasticsearch, fields);

        Assert.DoesNotContain(password, connectionString);
        Assert.Contains("pa%3Ass%40wo%3Ard%3Da%26b@es.internal:9200/", connectionString);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Elasticsearch, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal(password, parsed.Password);
    }

    [Theory]
    [InlineData("cache.internal", 9200, null, null)]
    [InlineData("cache.internal", 9201, "u", "p")]
    [InlineData("127.0.0.1", 9200, "user-name", "p@ss:word")]
    [InlineData("es.internal", 8443, "app=user", "pa%ss/word&x=y")]
    [InlineData("es.internal", 9200, "u", null)]
    [InlineData("es.internal", 9200, null, "p")]
    public void Parse_RoundTrips_Build(string host, int port, string? username, string? password)
    {
        var fields = new ManualConnectionFields(host, port, null, username, password, null);

        var connectionString = DatabaseConnectionStringFactory.Build(DatabaseProvider.Elasticsearch, fields);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Elasticsearch, connectionString);
        Assert.NotNull(parsed);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
        Assert.Equal(username, parsed.Username);
        Assert.Equal(password, parsed.Password);
        Assert.Null(parsed.Database);
        Assert.Null(parsed.FilePath);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("http://host:notaport/")]
    public void Parse_Unparseable_ReturnsNull(string connectionString)
    {
        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Elasticsearch, connectionString);

        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_Anonymous_ReturnsNoUserNoPass()
    {
        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Elasticsearch, "http://127.0.0.1:9200/");

        Assert.NotNull(parsed);
        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(9200, parsed.Port);
        Assert.Null(parsed.Username);
        Assert.Null(parsed.Password);
        Assert.Null(parsed.Database);
        Assert.Null(parsed.FilePath);
    }

    [Fact]
    public void Update_KeepsStoredHttpsScheme()
    {
        var entered = new ManualConnectionFields(
            Host: "new",
            Port: null,
            Database: null,
            Username: null,
            Password: null,
            FilePath: null);

        var updated = DatabaseConnectionStringFactory.Update(DatabaseProvider.Elasticsearch, "https://h:9200/", entered);

        Assert.Equal("https://new:9200/", updated);
    }

    [Fact]
    public void Update_BlankPassword_KeepsStored()
    {
        var entered = new ManualConnectionFields(
            Host: null,
            Port: null,
            Database: null,
            Username: "u",
            Password: null,
            FilePath: null);

        var updated = DatabaseConnectionStringFactory.Update(DatabaseProvider.Elasticsearch, "http://u:secret@h:9200/", entered);

        var parsed = DatabaseConnectionStringFactory.Parse(DatabaseProvider.Elasticsearch, updated);
        Assert.NotNull(parsed);
        Assert.Equal("secret", parsed.Password);
        Assert.Equal("u", parsed.Username);
    }
}
