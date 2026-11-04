using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The order the connection list is drawn in.
///
/// <para>
/// Four keys are offered and each can run in either direction, but the two
/// rules that are easy to get wrong are pinned here: a row the server has
/// never reported a reading for stays at the bottom in <em>both</em>
/// directions, and equal readings fall back to the name instead of the order
/// the rows happened to be registered in.
/// </para>
/// </summary>
public sealed class DatabaseConnectionSortTests
{
    private static readonly DateTimeOffset DefaultCreated =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DatabaseConnection Conn(
        string name,
        DatabaseProvider provider = DatabaseProvider.MySql,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastConnectedAt = null) =>
        new(
            "id-" + name,
            name,
            provider,
            "Server=localhost;Database=db;Uid=root;Pwd=pw",
            DatabaseEnvironment.ReadOnly,
            false,
            true,
            createdAt ?? DefaultCreated,
            lastConnectedAt);

    private static List<string> Names(IReadOnlyList<DatabaseConnection> rows) =>
        rows.Select(r => r.Name).ToList();

    [Fact]
    public void TryParseKey_Null_IsRefused()
    {
        Assert.False(DatabaseConnectionSort.TryParseKey(null, out var key));
        Assert.Equal("name", key);
    }

    [Fact]
    public void TryParseKey_Unknown_IsRefused()
    {
        Assert.False(DatabaseConnectionSort.TryParseKey("bogus", out var key));
        Assert.Equal("name", key);
    }

    [Fact]
    public void TryParseKey_Known_IsAccepted()
    {
        Assert.True(DatabaseConnectionSort.TryParseKey("name", out var name));
        Assert.Equal("name", name);

        Assert.True(DatabaseConnectionSort.TryParseKey("provider", out var provider));
        Assert.Equal("provider", provider);

        Assert.True(DatabaseConnectionSort.TryParseKey("created", out var created));
        Assert.Equal("created", created);

        Assert.True(DatabaseConnectionSort.TryParseKey("lastUsed", out var lastUsed));
        Assert.Equal("lastUsed", lastUsed);
    }

    [Fact]
    public void TryParseKey_IsCaseInsensitive_AndAnswersTheCanonicalSpelling()
    {
        Assert.True(DatabaseConnectionSort.TryParseKey("LASTUSED", out var key));
        Assert.Equal("lastUsed", key);

        Assert.True(DatabaseConnectionSort.TryParseKey("  Created  ", out var spaced));
        Assert.Equal("created", spaced);
    }

    [Fact]
    public void TryParseOrder_Null_IsRefused()
    {
        Assert.False(DatabaseConnectionSort.TryParseOrder(null, out var order));
        Assert.Equal(SortOrder.Ascending, order);
    }

    [Fact]
    public void TryParseOrder_Unknown_IsRefused()
    {
        Assert.False(DatabaseConnectionSort.TryParseOrder("sideways", out var order));
        Assert.Equal(SortOrder.Ascending, order);
    }

    [Fact]
    public void TryParseOrder_Accepted()
    {
        Assert.True(DatabaseConnectionSort.TryParseOrder("asc", out var asc));
        Assert.Equal(SortOrder.Ascending, asc);

        Assert.True(DatabaseConnectionSort.TryParseOrder("ascending", out var ascending));
        Assert.Equal(SortOrder.Ascending, ascending);

        Assert.True(DatabaseConnectionSort.TryParseOrder("desc", out var desc));
        Assert.Equal(SortOrder.Descending, desc);

        Assert.True(DatabaseConnectionSort.TryParseOrder("descending", out var descending));
        Assert.Equal(SortOrder.Descending, descending);
    }

    [Fact]
    public void Apply_ByName_OrdersCaseInsensitivelyAndReadsNumbersAsNumbers()
    {
        var rows = new List<DatabaseConnection>
        {
            Conn("beta"),
            Conn("alpha"),
            Conn("Alpha2"),
            Conn("Alpha10"),
        };

        Assert.Equal(
            new List<string> { "alpha", "Alpha2", "Alpha10", "beta" },
            Names(DatabaseConnectionSort.Apply(rows, "name", SortOrder.Ascending)));
    }

    [Fact]
    public void Apply_ByName_Descending_MirrorsTheColumn()
    {
        var rows = new List<DatabaseConnection>
        {
            Conn("beta"),
            Conn("alpha"),
            Conn("Alpha2"),
            Conn("Alpha10"),
        };

        Assert.Equal(
            new List<string> { "beta", "Alpha10", "Alpha2", "alpha" },
            Names(DatabaseConnectionSort.Apply(rows, "name", SortOrder.Descending)));
    }

    [Fact]
    public void Apply_ByProvider_UsesTheEnumOrder_ThenTheName()
    {
        var rows = new List<DatabaseConnection>
        {
            Conn("z-mysql", DatabaseProvider.MySql),
            Conn("a-sqlite", DatabaseProvider.Sqlite),
            Conn("b-redis", DatabaseProvider.Redis),
            Conn("a-mysql", DatabaseProvider.MySql),
        };

        Assert.Equal(
            new List<string> { "a-sqlite", "a-mysql", "z-mysql", "b-redis" },
            Names(DatabaseConnectionSort.Apply(rows, "provider", SortOrder.Ascending)));
    }

    [Fact]
    public void Apply_ByProvider_Descending_MirrorsTheColumn()
    {
        var rows = new List<DatabaseConnection>
        {
            Conn("z-mysql", DatabaseProvider.MySql),
            Conn("a-sqlite", DatabaseProvider.Sqlite),
            Conn("b-redis", DatabaseProvider.Redis),
            Conn("a-mysql", DatabaseProvider.MySql),
        };

        // The provider column is mirrored; inside one provider the name
        // tie-break still runs A to Z.
        Assert.Equal(
            new List<string> { "b-redis", "a-mysql", "z-mysql", "a-sqlite" },
            Names(DatabaseConnectionSort.Apply(rows, "provider", SortOrder.Descending)));
    }

    [Fact]
    public void Apply_ByCreated_PutsTheNewestFirst()
    {
        var rows = new List<DatabaseConnection>
        {
            Conn("jan", createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Conn("mar", createdAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            Conn("feb", createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        Assert.Equal(
            new List<string> { "mar", "feb", "jan" },
            Names(DatabaseConnectionSort.Apply(rows, "created", SortOrder.Ascending)));
    }

    [Fact]
    public void Apply_ByLastUsed_PutsTheMostRecentFirst()
    {
        var rows = new List<DatabaseConnection>
        {
            Conn("jan", lastConnectedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Conn("mar", lastConnectedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            Conn("feb", lastConnectedAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        Assert.Equal(
            new List<string> { "mar", "feb", "jan" },
            Names(DatabaseConnectionSort.Apply(rows, "lastUsed", SortOrder.Ascending)));
    }

    [Fact]
    public void Apply_ByLastUsed_KeepsNeverConnectedRowsLast_InBothDirections()
    {
        var rows = new List<DatabaseConnection>
        {
            Conn("never", lastConnectedAt: null),
            Conn("old", lastConnectedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Conn("new", lastConnectedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        Assert.Equal(
            new List<string> { "new", "old", "never" },
            Names(DatabaseConnectionSort.Apply(rows, "lastUsed", SortOrder.Ascending)));

        // The direction flips the readings, not the row that has none: a
        // connection the server never reported is unknown, not the oldest.
        Assert.Equal(
            new List<string> { "old", "new", "never" },
            Names(DatabaseConnectionSort.Apply(rows, "lastUsed", SortOrder.Descending)));
    }

    [Fact]
    public void Apply_EqualKeys_FallBackToTheName_InBothDirections()
    {
        var shared = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = new List<DatabaseConnection>
        {
            Conn("charlie", createdAt: shared),
            Conn("bravo", createdAt: shared),
            Conn("alpha", createdAt: shared),
        };

        Assert.Equal(
            new List<string> { "alpha", "bravo", "charlie" },
            Names(DatabaseConnectionSort.Apply(rows, "created", SortOrder.Ascending)));

        Assert.Equal(
            new List<string> { "alpha", "bravo", "charlie" },
            Names(DatabaseConnectionSort.Apply(rows, "created", SortOrder.Descending)));
    }

    [Fact]
    public void Apply_NeverMutatesTheInput()
    {
        var rows = new List<DatabaseConnection>
        {
            Conn("zebra"),
            Conn("alpha"),
        };

        DatabaseConnectionSort.Apply(rows, "name", SortOrder.Ascending);

        Assert.Equal(new List<string> { "zebra", "alpha" }, Names(rows));
    }

    [Fact]
    public void Apply_UnknownKey_SortsByName()
    {
        var rows = new List<DatabaseConnection>
        {
            Conn("zebra"),
            Conn("alpha"),
        };

        Assert.Equal(
            new List<string> { "alpha", "zebra" },
            Names(DatabaseConnectionSort.Apply(rows, "bogus", SortOrder.Ascending)));
    }
}
