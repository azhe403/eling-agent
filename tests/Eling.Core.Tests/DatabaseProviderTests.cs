using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// Redis is the third provider. Adding it must not have renumbered the two
/// that existed first - a registry that stores the number would break - so
/// the values are pinned here.
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
    public void AllThree_Present()
    {
        Assert.Equal(3, Enum.GetValues<DatabaseProvider>().Length);
    }
}
