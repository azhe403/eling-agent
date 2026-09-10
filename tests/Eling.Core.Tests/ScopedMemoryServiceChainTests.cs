namespace Eling.Core.Tests;

/// <summary>
/// Chain-aware ScopedMemoryService: the head level is the write target, merged
/// reads fan out over every level, and an empty chain blocks project saves.
/// </summary>
public sealed class ScopedMemoryServiceChainTests
{
    private const string ChildRoot = @"C:\work\acme\integrations\payments";
    private const string ParentRoot = @"C:\work\acme\integrations";

    private sealed class FakeMemoryService : IMemoryService
    {
        private readonly Dictionary<MemoryId, Memory> _items = new();

        public Task<SaveResult> SaveAsync(Memory memory)
        {
            _items[memory.Id] = memory;
            return Task.FromResult(new SaveResult(memory, SaveAction.Created));
        }

        public Task<Memory?> GetByIdAsync(MemoryId id)
            => Task.FromResult(_items.TryGetValue(id, out var m) ? m : null);

        public Task<Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null)
            => Task.FromResult(_items.TryGetValue(id, out var m) ? m : null);

        public Task<bool> DeleteAsync(MemoryId id)
            => Task.FromResult(_items.Remove(id));

        public Task<IReadOnlyCollection<Memory>> ListAllAsync()
            => Task.FromResult<IReadOnlyCollection<Memory>>(_items.Values.ToList());

        public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
            => Task.FromResult<IReadOnlyCollection<MemorySearchResult>>(Array.Empty<MemorySearchResult>());

        public Task RebuildIndexAsync() => Task.CompletedTask;

        public IReadOnlyCollection<Memory> All => _items.Values.ToList();
    }

    private static Memory NewMemory(string content) => new(MemoryType.Fact, content);

    private static (ScopedMemoryService Service, FakeMemoryService Child, FakeMemoryService Parent, FakeMemoryService Global) Build()
    {
        var child = new FakeMemoryService();
        var parent = new FakeMemoryService();
        var global = new FakeMemoryService();
        var service = new ScopedMemoryService(
            [
                new ProjectLevel(new ProjectScope(ChildRoot), child),
                new ProjectLevel(new ProjectScope(ParentRoot), parent)
            ],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ChildRoot);
        return (service, child, parent, global);
    }

    [Fact]
    public async Task SaveAsync_ProjectScope_Uninitialized_Throws()
    {
        var global = new FakeMemoryService();
        var service = new ScopedMemoryService(
            [],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ChildRoot);

        await Assert.ThrowsAsync<ProjectScopeNotInitializedException>(
            () => service.SaveAsync(NewMemory("x"), "project"));
        Assert.False(service.IsInitialized);
        Assert.Empty(service.ChainRoots);
    }

    [Fact]
    public async Task SaveAsync_Global_Uninitialized_StillWrites()
    {
        var global = new FakeMemoryService();
        var service = new ScopedMemoryService(
            [],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ChildRoot);

        var saved = await service.SaveAsync(NewMemory("global ok"), "global");

        Assert.Equal(MemoryScopeKind.Global, saved.Scope);
        Assert.Single(global.All);
    }

    [Fact]
    public async Task SaveAsync_Head_IsWriteTarget()
    {
        var (service, child, parent, global) = Build();

        var saved = await service.SaveAsync(NewMemory("default write"));

        Assert.Equal(MemoryScopeKind.Project, saved.Scope);
        Assert.Equal(ChildRoot, saved.ProjectRoot);
        Assert.Single(child.All);
    }

    [Fact]
    public async Task ListAsync_Merged_UsesChain()
    {
        var (service, child, parent, global) = Build();
        await service.SaveAsync(NewMemory("child mem"), "project");
        await parent.SaveAsync(NewMemory("parent mem"));
        await global.SaveAsync(NewMemory("global mem"));

        var merged = await service.ListAsync("merged");

        Assert.Equal(
            ["child mem", "parent mem", "global mem"],
            merged.Select(m => m.Memory.Content).ToList());
        Assert.Equal(
            [ChildRoot, ParentRoot, null],
            merged.Select(m => m.ProjectRoot).ToList());
    }

    [Fact]
    public async Task ListAsync_Project_OnlyHead()
    {
        var (service, child, parent, global) = Build();
        await service.SaveAsync(NewMemory("child mem"), "project");
        await parent.SaveAsync(NewMemory("parent mem"));

        var project = await service.ListAsync("project");

        var only = Assert.Single(project);
        Assert.Equal("child mem", only.Memory.Content);
        Assert.Equal(ChildRoot, only.ProjectRoot);
    }

    [Fact]
    public void ProjectService_Uninitialized_Throws()
    {
        var global = new FakeMemoryService();
        var service = new ScopedMemoryService(
            [],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ChildRoot);

        Assert.Throws<ProjectScopeNotInitializedException>(() => _ = service.ProjectService);
    }
}