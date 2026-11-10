using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The Redis read cache is the offline fallback: only the database list,
/// scan pages, and single-key inspections survive a dead server, marked
/// FromCache and no longer than a day. These tests pin the rules that make
/// that safe — an entry is scoped to its connection, to its database, and
/// when it is a page, to its prefix and cursor. A removed key stops being
/// served.
/// </summary>
public sealed class JsonRedisReadCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eling-redis-cache-tests", Guid.NewGuid().ToString("N"));

    private JsonRedisReadCache NewCache() => new(_dir);

    private static KeyInspectResult Inspection(string key) =>
        new(key, "string", -1, null, 5, "embstr", new KeyInspectPreview("string", [], false));

    private static bool TryDb(JsonRedisReadCache cache, string connectionId, out IReadOnlyList<RedisDatabaseInfo>? value, out DateTimeOffset? at)
        => cache.TryGetDatabases(connectionId, out value, out at);

    [Fact]
    public void Databases_RoundTrip()
    {
        var cache = NewCache();
        var expected = new List<RedisDatabaseInfo> { new(0, 3), new(3, 128) };

        cache.SetDatabases("c1", expected);

        Assert.True(TryDb(cache, "c1", out var value, out var at));
        Assert.Equal(expected, value);
        Assert.NotNull(at);
    }

    [Fact]
    public void Databases_UnknownConnection_IsAMiss()
    {
        var cache = NewCache();
        cache.SetDatabases("c1", [new RedisDatabaseInfo(0, 1)]);

        Assert.False(TryDb(cache, "c2", out var value, out var at));
        Assert.Null(value);
        Assert.Null(at);
    }

    [Fact]
    public void Databases_RefreshReplacesTheEarlierAnswer()
    {
        var cache = NewCache();
        cache.SetDatabases("c1", [new RedisDatabaseInfo(0, 1)]);
        cache.SetDatabases("c1", [new RedisDatabaseInfo(0, 9)]);

        Assert.True(TryDb(cache, "c1", out var value, out _));
        Assert.Equal(9, Assert.Single(value!).Keys);
    }

    [Fact]
    public void Keys_RoundTrip()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(3, out var db));

        cache.SetKey("c1", db, Inspection("session:abc"));

        Assert.True(cache.TryGetKey("c1", db, "session:abc", out var value, out var at));
        Assert.Equal("session:abc", value!.Key);
        Assert.Equal("string", value.Type);
        Assert.NotNull(at);
    }

    [Fact]
    public void Keys_AreScopedToTheirDatabase()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db0));
        Assert.True(RedisDatabaseIndex.TryCreate(1, out var db1));

        cache.SetKey("c1", db0, Inspection("k"));

        Assert.False(cache.TryGetKey("c1", db1, "k", out _, out _));
    }

    [Fact]
    public void Keys_AreScopedToTheirConnection()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));

        cache.SetKey("c1", db, Inspection("k"));

        Assert.False(cache.TryGetKey("c2", db, "k", out _, out _));
    }

    [Fact]
    public void RemoveKey_DropsOnlyThatKey()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        cache.SetKey("c1", db, Inspection("gone"));
        cache.SetKey("c1", db, Inspection("kept"));

        cache.RemoveKey("c1", db, "gone");

        Assert.False(cache.TryGetKey("c1", db, "gone", out _, out _));
        Assert.True(cache.TryGetKey("c1", db, "kept", out _, out _));
    }

    [Fact]
    public void InvalidateConnection_DropsEveryEntryForIt()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));
        cache.SetDatabases("c1", [new RedisDatabaseInfo(0, 1)]);
        cache.SetKey("c1", db, Inspection("k"));
        cache.SetKey("c2", db, Inspection("other"));

        cache.InvalidateConnection("c1");

        Assert.False(TryDb(cache, "c1", out _, out _));
        Assert.False(cache.TryGetKey("c1", db, "k", out _, out _));
        Assert.True(cache.TryGetKey("c2", db, "other", out _, out _));
    }

    [Fact]
    public void BlankInputs_AreIgnoredRatherThanStored()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));

        cache.SetDatabases(" ", [new RedisDatabaseInfo(0, 1)]);
        cache.SetKey("c1", db, Inspection(string.Empty));

        Assert.False(TryDb(cache, " ", out _, out _));
        Assert.False(cache.TryGetKey("c1", db, string.Empty, out _, out _));
    }

    [Fact]
    public void Pages_KeepAKeyThatLeftTheKeyspace_FlaggedExpired()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));

        // The page as it was: two keys.
        cache.SetPage("c1", db, "", 0, 0, 200, PageWithKeys("keep:1", "gone:1"));

        // The next scan no longer sees gone:1 - it expired.
        cache.SetPage("c1", db, "", 0, 0, 200, PageWithKeys("keep:1"));

        Assert.True(cache.TryGetPage("c1", db, "", 0, 0, 200, out var value, out _));
        var keys = value!.Keys;
        // The row stays in the tree instead of disappearing on its own.
        Assert.Equal(["keep:1", "gone:1"], keys.Select(k => k.Key));
        var expired = keys.Single(k => k.Key == "gone:1");
        Assert.True(expired.Expired);
        // Nothing is left to count down from, so the flag carries the meaning.
        Assert.Equal(-1, expired.TtlSeconds);
        Assert.Null(expired.ExpiresAt);
        // The key that is still there is not flagged.
        Assert.False(keys.Single(k => k.Key == "keep:1").Expired);
    }

    [Fact]
    public void Pages_DropTheFlagWhenTheKeyComesBack()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));

        cache.SetPage("c1", db, "", 0, 0, 200, PageWithKeys("keep:1"));
        cache.SetPage("c1", db, "", 0, 0, 200, PageWithKeys("keep:1", "back:1"));
        // back:1 vanishes, so it is carried as expired...
        cache.SetPage("c1", db, "", 0, 0, 200, PageWithKeys("keep:1"));
        Assert.True(cache.TryGetPage("c1", db, "", 0, 0, 200, out var expiredPage, out _));
        Assert.True(expiredPage!.Keys.Single(k => k.Key == "back:1").Expired);

        // ...and when the writer recreates it, the server's own row wins: no flag.
        cache.SetPage("c1", db, "", 0, 0, 200, PageWithKeys("keep:1", "back:1"));

        Assert.True(cache.TryGetPage("c1", db, "", 0, 0, 200, out var revived, out _));
        Assert.False(revived!.Keys.Single(k => k.Key == "back:1").Expired);
    }

    [Fact]
    public void Pages_DoNotInventKeysTheCacheNeverSaw()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db));

        // Nothing cached yet, so the first page is simply the server's answer.
        cache.SetPage("c1", db, "", 0, 0, 200, PageWithKeys("only:1"));

        Assert.True(cache.TryGetPage("c1", db, "", 0, 0, 200, out var value, out _));
        Assert.Equal(["only:1"], value!.Keys.Select(k => k.Key));
    }

    private static KeyPage PageWithKeys(params string[] keys) => new(
        "",
        [new KeyNode("session", "session:")],
        keys.Select(k => new KeyLeaf(k, "string", -1)).ToList(),
        Cursor: 0,
        PageOffset: 0,
        Complete: true,
        Scanned: keys.Length);

    private static KeyPage Page(string prefix) => new(
        prefix,
        [new KeyNode("session", "session:")],
        [new KeyLeaf("session:abc", "string", -1)],
        Cursor: 0,
        PageOffset: 0,
        Complete: true,
        Scanned: 1);

    [Fact]
    public void Pages_RoundTrip()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(3, out var db));

        cache.SetPage("c1", db, "session:", 0, 0, 200, Page("session:"));

        Assert.True(cache.TryGetPage("c1", db, "session:", 0, 0, 200, out var value, out var at));
        Assert.Equal(["session:abc"], value!.Keys.Select(k => k.Key));
        Assert.NotNull(at);
    }

    [Fact]
    public void Pages_AreScopedToTheirCoordinates()
    {
        var cache = NewCache();
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var db0));
        Assert.True(RedisDatabaseIndex.TryCreate(1, out var db1));

        cache.SetPage("c1", db0, "p:", 42, 0, 200, Page("p:"));

        Assert.False(cache.TryGetPage("c1", db1, "p:", 42, 0, 200, out _, out _));
        Assert.False(cache.TryGetPage("c2", db0, "p:", 42, 0, 200, out _, out _));
        Assert.False(cache.TryGetPage("c1", db0, "other:", 42, 0, 200, out _, out _));
        Assert.False(cache.TryGetPage("c1", db0, "p:", 43, 0, 200, out _, out _));
        Assert.False(cache.TryGetPage("c1", db0, "p:", 42, 0, 500, out _, out _));
        Assert.True(cache.TryGetPage("c1", db0, "p:", 42, 0, 200, out _, out _));
    }

    [Fact]
    public void AnUnreadableFileIsAnEmptyCache()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "database-redis-cache.json"), "{ not json");

        var cache = NewCache();

        Assert.False(TryDb(cache, "c1", out _, out _));
        cache.SetDatabases("c1", [new RedisDatabaseInfo(0, 2)]);
        Assert.True(TryDb(cache, "c1", out _, out _));
    }

    [Fact]
    public void TwoEntriesOverTheSameName_StillWriteCleanly()
    {
        // The kernel object is shared by name, so two caches in one process
        // (the dashboard and a test class, say) hold the same gate. If each
        // built its own Mutex, ReleaseMutex would throw "unsynchronized block"
        // on the second owner and the gate would wedge for the process.
        //
        // Sequential on purpose: a parallel burst here holds a process-wide
        // lock long enough to starve the timing-sensitive audit-logger tests
        // in this collection. What matters is that two owners never break the
        // gate, not that they run at once.
        var first = NewCache();
        var second = NewCache();

        for (var i = 0; i < 20; i++)
        {
            var cache = i % 2 == 0 ? first : second;
            cache.SetDatabases($"c{i % 5}", [new RedisDatabaseInfo(0, i)]);
        }

        Assert.True(TryDb(first, "c4", out var value, out _));
        Assert.Equal(19, Assert.Single(value!).Keys);
        Assert.True(TryDb(second, "c3", out var other, out _));
        Assert.Equal(18, Assert.Single(other!).Keys);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
