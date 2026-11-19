using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// Redis is the third provider. Adding it must not have renumbered the two
/// that existed first - a registry that stores the number would break - so
/// the values are pinned here. PostgreSql is the fourth; it is appended, not
/// inserted. Elasticsearch is the fifth, likewise appended.
/// </summary>
public sealed class DatabaseProviderTests
{
    [Fact]
    public void Redis_IsTwo()
    {
        Assert.Equal(2, (int)DatabaseProvider.Redis);
    }

    [Fact]
    public void TheOriginalProviders_KeptTheirValues()
    {
        Assert.Equal(0, (int)DatabaseProvider.Sqlite);
        Assert.Equal(1, (int)DatabaseProvider.MySql);
    }

    [Fact]
    public void PostgreSql_IsAppended_NotInserted()
    {
        Assert.Equal(3, (int)DatabaseProvider.PostgreSql);
    }

    [Fact]
    public void Elasticsearch_IsFour()
    {
        Assert.Equal(4, (int)DatabaseProvider.Elasticsearch);
    }

    [Fact]
    public void Existing_Values_Unchanged()
    {
        Assert.Equal(0, (int)DatabaseProvider.Sqlite);
        Assert.Equal(1, (int)DatabaseProvider.MySql);
        Assert.Equal(2, (int)DatabaseProvider.Redis);
        Assert.Equal(3, (int)DatabaseProvider.PostgreSql);
    }

    [Fact]
    public void AllFive_Present()
    {
        Assert.Equal(5, Enum.GetValues<DatabaseProvider>().Length);
    }
}
