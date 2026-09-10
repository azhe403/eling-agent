namespace Eling.Core.Tests;

/// <summary>
/// N-level level-grouped merge contract: blocks appear nearest-first
/// (own scope, ancestors, then global) with per-block input order preserved,
/// and nearest-wins dedup by ULID.
/// </summary>
public sealed class MemoryMergerChainTests
{
    private const string ChildRoot = @"C:\work\acme\integrations\payments";
    private const string ParentRoot = @"C:\work\acme\integrations";

    private static Memory NewMemory(string content, MemoryId? id = null)
        => new(MemoryType.Fact, content, id: id);

    private static MemorySearchResult NewResult(MemoryId id, double rank)
        => new(id, rank, MatchedVia: ["porter"]);

    [Fact]
    public void MergeLists_LevelGrouped_NearestFirst()
    {
        var childA = NewMemory("child A");
        var childB = NewMemory("child B");
        var parent = NewMemory("parent");
        var global1 = NewMemory("global 1");
        var global2 = NewMemory("global 2");

        var merged = new MemoryMerger().MergeLists(
            [new MemoryLevel(ChildRoot, [childA, childB]), new MemoryLevel(ParentRoot, [parent])],
            [global1, global2]);

        Assert.Equal(
            [childA.Id.Value, childB.Id.Value, parent.Id.Value, global1.Id.Value, global2.Id.Value],
            merged.Select(m => m.Id.Value).ToList());
        Assert.Equal(
            [ChildRoot, ChildRoot, ParentRoot, null, null],
            merged.Select(m => m.ProjectRoot).ToList());
        Assert.All(merged.Take(3), m => Assert.Equal(MemoryScopeKind.Project, m.Scope));
        Assert.All(merged.Skip(3), m => Assert.Equal(MemoryScopeKind.Global, m.Scope));
    }

    [Fact]
    public void MergeLists_SameUlidAtTwoLevels_KeepsNearest()
    {
        var id = MemoryId.NewId();
        var childCopy = NewMemory("child copy", id);
        var parentCopy = NewMemory("parent copy", id);

        var merged = new MemoryMerger().MergeLists(
            [new MemoryLevel(ChildRoot, [childCopy]), new MemoryLevel(ParentRoot, [parentCopy])],
            []);

        var single = Assert.Single(merged);
        Assert.Equal(ChildRoot, single.ProjectRoot);
        Assert.Equal("child copy", single.Memory.Content);
    }

    [Fact]
    public void MergeLists_SameUlidProjectAndGlobal_KeepsProject()
    {
        var id = MemoryId.NewId();
        var projectCopy = NewMemory("project copy", id);
        var globalCopy = NewMemory("global copy", id);

        var merged = new MemoryMerger().MergeLists(
            [new MemoryLevel(ParentRoot, [projectCopy])],
            [globalCopy]);

        var single = Assert.Single(merged);
        Assert.Equal(MemoryScopeKind.Project, single.Scope);
        Assert.Equal(ParentRoot, single.ProjectRoot);
        Assert.Equal("project copy", single.Memory.Content);
    }

    [Fact]
    public void MergeSearchResults_LevelGrouped()
    {
        var childStrong = NewResult(MemoryId.NewId(), rank: -50);
        var childWeak = NewResult(MemoryId.NewId(), rank: -20);
        var parentStronger = NewResult(MemoryId.NewId(), rank: -500);
        var global = NewResult(MemoryId.NewId(), rank: -10);

        var merged = new MemoryMerger().MergeSearchResults(
            [
                new SearchResultLevel(ChildRoot, [childStrong, childWeak]),
                new SearchResultLevel(ParentRoot, [parentStronger])
            ],
            [global]);

        // Child block precedes parent even though the parent hit ranks stronger;
        // within a block ascending rank; global always last.
        Assert.Equal(
            [childStrong.Id.Value, childWeak.Id.Value, parentStronger.Id.Value, global.Id.Value],
            merged.Select(r => r.Id.Value).ToList());
        Assert.Equal(
            [ChildRoot, ChildRoot, ParentRoot, null],
            merged.Select(r => r.ProjectRoot).ToList());
    }

    [Fact]
    public void MergeSearchResults_N1_ListWrapper_MatchesOldOrdering()
    {
        var projectA = NewResult(MemoryId.NewId(), rank: -200);
        var projectB = NewResult(MemoryId.NewId(), rank: -100);
        var global = NewResult(MemoryId.NewId(), rank: -10);

        var merger = new MemoryMerger();
        var viaOld = merger.MergeSearchResults([projectA, projectB], [global], ParentRoot);
        var viaNew = merger.MergeSearchResults(
            [new SearchResultLevel(ParentRoot, [projectA, projectB])],
            [global]);

        Assert.Equal(viaNew.Select(r => r.Id.Value).ToList(), viaOld.Select(r => r.Id.Value).ToList());
        Assert.All(viaOld.Take(2), r => Assert.Equal(ParentRoot, r.ProjectRoot));
        Assert.Equal(MemoryScopeKind.Global, viaOld.Last().Scope);
        Assert.Null(viaOld.Last().ProjectRoot);
    }
}