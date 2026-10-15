using Eling.Core.Memory;
using Eling.Core.Memory.Serialization;
using Eling.Core.MemoryRecall;
using Eling.Core.Scope;

namespace Eling.Core.Tests;

/// <summary>
/// Recall provenance: topic hits and recent items carry the scope level
/// (own root, ancestor root, or null for global) they were recalled from.
/// </summary>
public sealed class MemoryRecallServiceChainTests
{
    private const string ChildRoot = @"C:\work\acme\integrations\payments";
    private const string ParentRoot = @"C:\work\acme\integrations";

    private sealed class FakeRecallMemoryService : IMemoryService
    {
        private readonly Dictionary<MemoryId, Memory.Memory> _items = new();
        private readonly Dictionary<string, MemorySearchResult> _searchHits = new();

        public Task<MemorySaveResult> SaveAsync(Memory.Memory memory)
        {
            _items[memory.Id] = memory;
            return Task.FromResult(new MemorySaveResult(memory, SaveAction.Created));
        }

        public Task<Memory.Memory?> GetByIdAsync(MemoryId id)
            => Task.FromResult(_items.TryGetValue(id, out var m) ? m : null);

        public Task<Memory.Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null)
            => Task.FromResult(_items.TryGetValue(id, out var m) ? m : null);

        public Task<bool> DeleteAsync(MemoryId id)
            => Task.FromResult(_items.Remove(id));

        public Task<IReadOnlyCollection<Memory.Memory>> ListAllAsync()
            => Task.FromResult<IReadOnlyCollection<Memory.Memory>>(_items.Values.ToList());

        public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
        {
            var rank = -1000.0;
            var results = _items.Values
                .Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(m => new MemorySearchResult(m.Id, rank += 1.0, MatchedVia: ["porter"]))
                .ToList();
            return Task.FromResult<IReadOnlyCollection<MemorySearchResult>>(results);
        }

        public Task RebuildIndexAsync() => Task.CompletedTask;
    }

    private sealed class EmptyIntentionStorage : IIntentionStorage
    {
        public Task SaveAsync(Intention.Intention intention) => Task.CompletedTask;
        public Task<Intention.Intention?> GetByIdAsync(MemoryId id) => Task.FromResult<Intention.Intention?>(null);
        public Task<bool> DeleteAsync(MemoryId id) => Task.FromResult(true);
        public Task<IReadOnlyCollection<Intention.Intention>> ListAllAsync()
            => Task.FromResult<IReadOnlyCollection<Intention.Intention>>(Array.Empty<Intention.Intention>());
    }

    private static (ScopedMemoryService Scoped, FakeRecallMemoryService Child, FakeRecallMemoryService Parent, FakeRecallMemoryService Global) Build()
    {
        var child = new FakeRecallMemoryService();
        var parent = new FakeRecallMemoryService();
        var global = new FakeRecallMemoryService();
        var scoped = new ScopedMemoryService(
            [
                new ProjectLevel(new ProjectScope(ChildRoot), child),
                new ProjectLevel(new ProjectScope(ParentRoot), parent)
            ],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ChildRoot);
        return (scoped, child, parent, global);
    }

    [Fact]
    public async Task Recall_Merged_AncestorHitCarriesAncestorRoot()
    {
        var (scoped, _, parent, _) = Build();
        await parent.SaveAsync(new Memory.Memory(MemoryType.Fact, "parent topic"));

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["topic"], null, null));

        var hit = Assert.Single(result.RecallMemories);
        Assert.Equal(MemoryScopeKind.Project, hit.Scope);
        Assert.Equal(ParentRoot, hit.ProjectRoot);
    }

    [Fact]
    public async Task Recall_Merged_OwnHitCarriesOwnRoot()
    {
        var (scoped, child, _, _) = Build();
        await child.SaveAsync(new Memory.Memory(MemoryType.Fact, "own topic"));

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["topic"], null, null));

        var hit = Assert.Single(result.RecallMemories);
        Assert.Equal(MemoryScopeKind.Project, hit.Scope);
        Assert.Equal(ChildRoot, hit.ProjectRoot);
    }

    [Fact]
    public async Task Recall_Merged_LimitReached_AncestorAndGlobalHitsAreNotStarved()
    {
        var (scoped, child, parent, global) = Build();
        for (var i = 0; i < 10; i++)
        {
            await child.SaveAsync(new Memory.Memory(MemoryType.Fact, $"topic child {i}"));
        }
        await parent.SaveAsync(new Memory.Memory(MemoryType.Fact, "topic parent 1"));
        await parent.SaveAsync(new Memory.Memory(MemoryType.Fact, "topic parent 2"));
        await global.SaveAsync(new Memory.Memory(MemoryType.Fact, "topic global 1"));

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(
            new MemoryRecallContext(["topic"], null, null),
            recallLimit: 10);

        Assert.Equal(10, result.RecallMemories.Count);
        // Guarantee: the crowded head level must not starve ancestor/global hits.
        Assert.Contains(result.RecallMemories, h => h.ProjectRoot == ParentRoot);
        Assert.Contains(result.RecallMemories, h => h.Scope == MemoryScopeKind.Global);
        // Interleave: a level with several relevant hits surfaces all of them,
        // not just the single guaranteed slot.
        Assert.Equal(2, result.RecallMemories.Count(h => h.ProjectRoot == ParentRoot));
        Assert.Equal(ChildRoot, result.RecallMemories[0].ProjectRoot);
    }

    [Fact]
    public async Task Recall_Merged_LimitReached_LevelWithoutHitsLeavesNoEmptySlot()
    {
        var (scoped, child, parent, global) = Build();
        for (var i = 0; i < 10; i++)
        {
            await child.SaveAsync(new Memory.Memory(MemoryType.Fact, $"topic child {i}"));
        }
        await parent.SaveAsync(new Memory.Memory(MemoryType.Fact, "unrelated parent note"));
        await global.SaveAsync(new Memory.Memory(MemoryType.Fact, "topic global 1"));

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(
            new MemoryRecallContext(["topic"], null, null),
            recallLimit: 10);

        // No quota is reserved for a level without hits: the limit is still filled.
        Assert.Equal(10, result.RecallMemories.Count);
        Assert.Contains(result.RecallMemories, h => h.Scope == MemoryScopeKind.Global);
    }

    [Fact]
    public async Task Recall_Recent_KeepsScopedProvenance()
    {
        var (scoped, child, _, global) = Build();
        await child.SaveAsync(new Memory.Memory(MemoryType.Fact, "project recent"));
        await global.SaveAsync(new Memory.Memory(MemoryType.Fact, "global recent"));

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(context: null, recentLimit: 10);

        var projectRecent = Assert.Single(result.RecentMemories, m => m.Memory.Content == "project recent");
        Assert.Equal(ChildRoot, projectRecent.ProjectRoot);
        var globalRecent = Assert.Single(result.RecentMemories, m => m.Memory.Content == "global recent");
        Assert.Equal(MemoryScopeKind.Global, globalRecent.Scope);
        Assert.Null(globalRecent.ProjectRoot);
    }

    [Fact]
    public async Task Recall_StandingBaseline_SurfacesEvenWhenTopicMisses()
    {
        var (scoped, child, _, _) = Build();
        await child.SaveAsync(new Memory.Memory(MemoryType.Preference, "always run git hygiene before review"));

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["zzz-nomatch"], null, null));

        Assert.Empty(result.RecallMemories);
        var standing = Assert.Single(result.StandingMemories!);
        Assert.Equal("standing-baseline", standing.QueryMode);
        Assert.Contains("standing", standing.MatchedVia!);
        Assert.Equal(MemoryType.Preference, standing.Memory.Type);
    }

    [Fact]
    public async Task Recall_StandingBaseline_IgnoresNonStandingTypes()
    {
        var (scoped, child, _, _) = Build();
        await child.SaveAsync(new Memory.Memory(MemoryType.Fact, "a plain fact"));

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["fact"], null, null));

        Assert.Single(result.RecallMemories);
        Assert.Empty(result.StandingMemories!);
    }

    [Fact]
    public async Task Recall_StandingBaseline_CapsAtFive()
    {
        var (scoped, child, _, _) = Build();
        for (var i = 0; i < 7; i++)
        {
            await child.SaveAsync(new Memory.Memory(MemoryType.Preference, $"rule {i}"));
        }

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["zzz-nomatch"], null, null));

        Assert.Equal(5, result.StandingMemories!.Count);
    }

    [Fact]
    public async Task Recall_StandingBaseline_ExcludesMemoriesAlreadyInTopicHits()
    {
        var (scoped, child, _, _) = Build();
        await child.SaveAsync(new Memory.Memory(MemoryType.Decision, "decision about topic"));

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["topic"], null, null));

        Assert.Single(result.RecallMemories);
        Assert.Empty(result.StandingMemories!);
    }

    [Fact]
    public async Task Recall_StandingBaseline_CurrentProjectOutranksNewerParent()
    {
        var (scoped, child, parent, _) = Build();
        await child.SaveAsync(new Memory.Memory(MemoryType.Preference, "child rule"));
        await parent.SaveAsync(new Memory.Memory(
            MemoryType.Preference, "parent rule", updatedAt: DateTimeOffset.UtcNow.AddHours(1)));

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["zzz-nomatch"], null, null));

        Assert.Equal(2, result.StandingMemories!.Count);
        Assert.Equal(ChildRoot, result.StandingMemories[0].ProjectRoot);
        Assert.Equal(ParentRoot, result.StandingMemories[1].ProjectRoot);
    }

    [Fact]
    public async Task Recall_Overflow_BeyondLimit_ReportedNotDropped()
    {
        var (scoped, child, _, _) = Build();
        for (var i = 0; i < 12; i++)
        {
            await child.SaveAsync(new Memory.Memory(MemoryType.Fact, $"topic item {i}"));
        }

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["topic"], null, null), recallLimit: 10);

        Assert.Equal(10, result.RecallMemories.Count);
        Assert.Equal(2, result.OverflowMemories!.Count);
        var recallIds = result.RecallMemories.Select(h => h.Memory.Id).ToHashSet();
        Assert.All(result.OverflowMemories, h => Assert.DoesNotContain(h.Memory.Id, recallIds));
    }

    [Fact]
    public async Task Recall_Overflow_BoundedByWindow()
    {
        var (scoped, child, _, _) = Build();
        for (var i = 0; i < 25; i++)
        {
            await child.SaveAsync(new Memory.Memory(MemoryType.Fact, $"topic item {i}"));
        }

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["topic"], null, null), recallLimit: 10);

        Assert.Equal(10, result.RecallMemories.Count);
        Assert.Equal(10, result.OverflowMemories!.Count);
    }

    [Fact]
    public async Task Recall_NoOverflow_WhenHitsWithinLimit()
    {
        var (scoped, child, _, _) = Build();
        for (var i = 0; i < 4; i++)
        {
            await child.SaveAsync(new Memory.Memory(MemoryType.Fact, $"topic item {i}"));
        }

        var service = new MemoryRecallService(scoped, new EmptyIntentionStorage());
        var result = await service.RecallAsync(new MemoryRecallContext(["topic"], null, null), recallLimit: 10);

        Assert.Equal(4, result.RecallMemories.Count);
        Assert.Null(result.OverflowMemories);
    }
}