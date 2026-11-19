using System.Text.Json;
using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The filter compiler turns rows decoded from a query string ("filters=[{field,op,value}]")
/// into the Elasticsearch "bool" DSL that drops in as a filter clause list. It is a pure
/// JSON builder: no ES client, no network, and the field string is used exactly as given -
/// the compiler never appends ".keyword"; the UI offers it explicitly.
/// </summary>
public sealed class ElasticsearchFilterCompilerTests
{
    [Fact]
    public void Is_Term()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "kubernetes.container_name.keyword",
                Op: "is",
                Value: "api"),
        };

        var result = ElasticsearchFilterCompiler.Compile(rows, logic: null);

        Assert.Equal(
            """{"bool":{"must":[{"term":{"kubernetes.container_name.keyword":{"value":"api"}}}]}}""",
            result.GetRawText());
    }

    [Fact]
    public void Contains_MatchAnd()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "msg",
                Op: "contains",
                Value: "login failed"),
        };

        var result = ElasticsearchFilterCompiler.Compile(rows, logic: null);

        Assert.Equal(
            """{"bool":{"must":[{"match":{"msg":{"query":"login failed","operator":"and"}}}]}}""",
            result.GetRawText());
    }

    [Theory]
    [InlineData("gt")]
    [InlineData("gte")]
    [InlineData("lt")]
    [InlineData("lte")]
    public void Range_Number(string op)
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "@timestamp",
                Op: op,
                Value: "2026-01-01"),
        };

        var result = ElasticsearchFilterCompiler.Compile(rows, logic: null);

        Assert.Equal(
            "{\"bool\":{\"must\":[{\"range\":{\"@timestamp\":{\"" + op + "\":\"2026-01-01\"}}}]}}",
            result.GetRawText());
    }

    [Fact]
    public void Exists_BoolField()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "user.id",
                Op: "exists",
                Value: null),
        };

        var result = ElasticsearchFilterCompiler.Compile(rows, logic: null);

        Assert.Equal(
            """{"bool":{"must":[{"exists":{"field":"user.id"}}]}}""",
            result.GetRawText());
    }

    [Fact]
    public void And_CombinesBoolMust()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "msg",
                Op: "contains",
                Value: "login"),
            new ElasticsearchFilterRow(
                Field: "level",
                Op: "is",
                Value: "error"),
        };

        var result = ElasticsearchFilterCompiler.Compile(rows, logic: "and");

        Assert.Equal(
            """{"bool":{"must":[{"match":{"msg":{"query":"login","operator":"and"}}},{"term":{"level":{"value":"error"}}}]}}""",
            result.GetRawText());
    }

    [Fact]
    public void Or_CombinesBoolShouldMinMatch1()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "msg",
                Op: "contains",
                Value: "login"),
            new ElasticsearchFilterRow(
                Field: "level",
                Op: "is",
                Value: "error"),
        };

        var result = ElasticsearchFilterCompiler.Compile(rows, logic: "or");

        Assert.Equal(
            """{"bool":{"should":[{"match":{"msg":{"query":"login","operator":"and"}}},{"term":{"level":{"value":"error"}}}],"minimum_should_match":1}}""",
            result.GetRawText());
    }

    [Theory]
    [InlineData("msg or")]
    [InlineData("")]
    [InlineData("a/b")]
    public void BadFieldCharset_Throws(string field)
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: field,
                Op: "is",
                Value: "x"),
        };

        Assert.Throws<ElasticsearchFilterException>(() => ElasticsearchFilterCompiler.Compile(rows, logic: null));
    }

    [Fact]
    public void BadFieldLength_Throws()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: new string('a', 300),
                Op: "is",
                Value: "x"),
        };

        Assert.Throws<ElasticsearchFilterException>(() => ElasticsearchFilterCompiler.Compile(rows, logic: null));
    }

    [Fact]
    public void BadOp_Throws()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "msg",
                Op: "matches",
                Value: "x"),
        };

        Assert.Throws<ElasticsearchFilterException>(() => ElasticsearchFilterCompiler.Compile(rows, logic: null));
    }

    [Fact]
    public void ExistsWithValue_Throws()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "user.id",
                Op: "exists",
                Value: "anything"),
        };

        Assert.Throws<ElasticsearchFilterException>(() => ElasticsearchFilterCompiler.Compile(rows, logic: null));
    }

    [Fact]
    public void RangeWithNonNumeric_Throws()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "@timestamp",
                Op: "gte",
                Value: "abc"),
        };

        Assert.Throws<ElasticsearchFilterException>(() => ElasticsearchFilterCompiler.Compile(rows, logic: null));
    }

    [Fact]
    public void RangeWithEpochMs_Accepts()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "@timestamp",
                Op: "gte",
                Value: "1760000000000"),
        };

        var result = ElasticsearchFilterCompiler.Compile(rows, logic: null);

        Assert.Contains("\"gte\":\"1760000000000\"", result.GetRawText());
    }

    [Fact]
    public void RangeWithNullValue_Throws()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "@timestamp",
                Op: "gte",
                Value: null),
        };

        var exception = Assert.Throws<ElasticsearchFilterException>(
            () => ElasticsearchFilterCompiler.Compile(rows, logic: null));
        Assert.Equal("range requires a value", exception.Message);
    }

    [Fact]
    public void IsWithNullValue_Throws()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "level",
                Op: "is",
                Value: null),
        };

        Assert.Throws<ElasticsearchFilterException>(() => ElasticsearchFilterCompiler.Compile(rows, logic: null));
    }

    [Fact]
    public void EmptyRows_Throws()
    {
        var exception = Assert.Throws<ElasticsearchFilterException>(
            () => ElasticsearchFilterCompiler.Compile(Array.Empty<ElasticsearchFilterRow>(), logic: null));

        Assert.Equal("at least one filter row is required", exception.Message);
    }

    [Fact]
    public void FirstInvalidRowWins_Throws()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "ok.field",
                Op: "matches",
                Value: "x"),
            new ElasticsearchFilterRow(
                Field: "also bad field!",
                Op: "is",
                Value: "y"),
        };

        var exception = Assert.Throws<ElasticsearchFilterException>(
            () => ElasticsearchFilterCompiler.Compile(rows, logic: null));

        Assert.Contains("matches", exception.Message);
    }

    [Fact]
    public void Compiled_Json_RoundTrips()
    {
        var rows = new[]
        {
            new ElasticsearchFilterRow(
                Field: "kubernetes.container_name.keyword",
                Op: "is",
                Value: "api"),
            new ElasticsearchFilterRow(
                Field: "@timestamp",
                Op: "gte",
                Value: "2026-01-01"),
            new ElasticsearchFilterRow(
                Field: "user.id",
                Op: "exists",
                Value: null),
        };

        var result = ElasticsearchFilterCompiler.Compile(rows, logic: "or");
        var raw = result.GetRawText();

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        var orClause = root.GetProperty("bool").GetProperty("should");
        Assert.Equal(3, orClause.GetArrayLength());
        Assert.Equal(1, root.GetProperty("bool").GetProperty("minimum_should_match").GetInt32());
        Assert.False(root.GetProperty("bool").TryGetProperty("must", out _));

        Assert.Equal(
            "api",
            orClause[0].GetProperty("term").GetProperty("kubernetes.container_name.keyword").GetProperty("value").GetString());
        Assert.Equal(
            "2026-01-01",
            orClause[1].GetProperty("range").GetProperty("@timestamp").GetProperty("gte").GetString());
        Assert.Equal(
            "user.id",
            orClause[2].GetProperty("exists").GetProperty("field").GetString());

        // No .keyword magic is appended by the compiler.
        Assert.DoesNotContain("api.keyword", raw);
    }
}
