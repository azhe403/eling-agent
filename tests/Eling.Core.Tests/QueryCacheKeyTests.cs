using Eling.Core.Database;

namespace Eling.Core.Tests;

public sealed class QueryCacheKeyTests
{
    private static readonly IReadOnlyList<SqlParam> P1 = [new SqlParam("@p0", "a")];
    private static readonly IReadOnlyList<SqlParam> P2 = [new SqlParam("@p0", "b")];

    [Fact]
    public void QueryCacheKey_NoParameters_IsTheSqlUnchanged()
    {
        Assert.Equal("SELECT 1", QueryCacheKey.For("SELECT 1", null));
        Assert.Equal("SELECT 1", QueryCacheKey.For("SELECT 1", []));
    }

    [Fact]
    public void QueryCacheKey_DifferentValues_DifferentKeys()
    {
        Assert.NotEqual(
            QueryCacheKey.For("SELECT 1 WHERE x = @p0", P1),
            QueryCacheKey.For("SELECT 1 WHERE x = @p0", P2));
    }

    [Fact]
    public void QueryCacheKey_TypeTagsDistinguishStringFromInt()
    {
        var asString = QueryCacheKey.For("q", [new SqlParam("@p0", "1")]);
        var asInt = QueryCacheKey.For("q", [new SqlParam("@p0", 1)]);

        Assert.NotEqual(asString, asInt);
    }

    [Fact]
    public void QueryCacheKey_IsStableForSameInput()
    {
        Assert.Equal(
            QueryCacheKey.For("q", P1),
            QueryCacheKey.For("q", [new SqlParam("@p0", "a")]));
    }

    [Fact]
    public void QueryCacheKey_MultipleParameters_AreAllCaptured()
    {
        var key = QueryCacheKey.For("q", [new SqlParam("@p0", "a"), new SqlParam("@p1", 2)]);

        Assert.Contains("@p0=s:1:a", key);
        Assert.Contains("@p1=i:1:2", key);
    }

    [Fact]
    public void QueryCacheKey_PrefixSeparatorsInValues_CannotCollide()
    {
        // A value carrying separators and length-like text yields a key no
        // other value can forge: the length prefix pins the representation.
        var a = QueryCacheKey.For("q", [new SqlParam("@p0", "=s:1:a\u001f@p1=i:1:2")]);
        var b = QueryCacheKey.For("q", [new SqlParam("@p0", "a"), new SqlParam("@p1", "2")]);

        Assert.NotEqual(a, b);
    }
}
