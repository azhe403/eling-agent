using Eling.Core.Exceptions;
using Eling.Core.Memory;
using Eling.Core.Scope;

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
        private readonly Dictionary<MemoryId, Memory.Memory> _items = new();

        public Task<SaveResult> SaveAsync(Memory.Memory memory)
        {
            _items[memory.Id] = memory;
            return Task.FromResult(new SaveResult(memory, SaveAction.Created));
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
            => Task.FromResult<IReadOnlyCollection<MemorySearchResult>>(Array.Empty<MemorySearchResult>());

        public bool RebuildIndexCalled;

        public Task RebuildIndexAsync()
        {
            RebuildIndexCalled = true;
            return Task.CompletedTask;
        }

        public IReadOnlyCollection<Memory.Memory> All => _items.Values.ToList();
    }

    private static Memory.Memory NewMemory(string content) => new(MemoryType.Fact, content);

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

    // ---------- Ancestor targeting ----------

    [Fact]
    public void ResolveAncestorProjectRoot_WithOwnScope_ResolvesParent()
    {
        var (service, _, _, _) = Build();

        var root = service.ResolveAncestorProjectRoot("integrations");

        Assert.Equal(ParentRoot, root);
        Assert.True(service.HasOwnScope);
    }

    [Fact]
    public void ResolveAncestorProjectRoot_UnknownName_Throws()
    {
        var (service, _, _, _) = Build();

        var ex = Assert.Throws<InvalidProjectTargetException>(() => service.ResolveAncestorProjectRoot("nope"));

        Assert.Contains("integrations", ex.AvailableProjectNames);
    }

    [Fact]
    public void ResolveAncestorProjectRoot_WithoutOwnScope_Throws()
    {
        var service = new ScopedMemoryService(
            [new ProjectLevel(new ProjectScope(ParentRoot), new FakeMemoryService())],
            new FakeMemoryService(),
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ChildRoot);

        Assert.Throws<InvalidProjectTargetException>(() => service.ResolveAncestorProjectRoot("integrations"));
        Assert.False(service.HasOwnScope);
    }

    [Fact]
    public async Task SaveToProjectAsync_WritesToAncestorLevel()
    {
        var (service, child, parent, _) = Build();

        var saved = await service.SaveToProjectAsync(NewMemory("parent write"), ParentRoot);

        Assert.Equal(ParentRoot, saved.ProjectRoot);
        Assert.Single(parent.All);
        Assert.Empty(child.All);
    }

    [Fact]
    public async Task GetByIdAsync_AncestorReference_ReadsAncestorOnly()
    {
        var (service, child, parent, _) = Build();
        var memory = NewMemory("parent mem");
        await parent.SaveAsync(memory);

        var found = await service.GetByIdAsync(MemoryReference.ForProject(memory.Id, ParentRoot));

        Assert.NotNull(found);
        Assert.Equal(ParentRoot, found!.ProjectRoot);
        Assert.Empty(child.All);
    }

    [Fact]
    public async Task DeleteAsync_AncestorReference_DeletesAncestorOnly()
    {
        var (service, child, parent, _) = Build();
        var memory = NewMemory("parent mem");
        await parent.SaveAsync(memory);

        var deleted = await service.DeleteAsync(MemoryReference.ForProject(memory.Id, ParentRoot));

        Assert.True(deleted);
        Assert.Empty(parent.All);
        Assert.Empty(child.All);
    }

    [Fact]
    public async Task RebuildProjectIndexAsync_TargetsAncestorLevel()
    {
        var (service, child, parent, _) = Build();

        await service.RebuildProjectIndexAsync(ParentRoot);

        Assert.True(parent.RebuildIndexCalled);
        Assert.False(child.RebuildIndexCalled);
    }
}