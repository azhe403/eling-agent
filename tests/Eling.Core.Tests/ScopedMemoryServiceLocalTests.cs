using Eling.Core.Memory;
using Eling.Core.Scope;

namespace Eling.Core.Tests;

/// <summary>
/// Project-local tier: explicit-only saves land in the canonical shard,
/// merged reads order local &gt; project &gt; global, and an unwired local
/// service fails writes loudly while reads stay empty.
/// </summary>
public sealed class ScopedMemoryServiceLocalTests
{
    private const string ProjectRoot = @"C:\work\acme";
    private const string CanonicalRoot = @"C:\work\acme";

    private sealed class FakeMemoryService : IMemoryService
    {
        private readonly Dictionary<MemoryId, Memory.Memory> _items = new();
        public List<MemorySearchResult> SearchHits { get; } = [];

        public Task<MemorySaveResult> SaveAsync(Memory.Memory memory)
        {
            _items[memory.Id] = memory;
            return Task.FromResult(new MemorySaveResult(memory, SaveAction.Created));
        }

        public Task<Memory.Memory?> FindActiveSimilarAsync(Memory.Memory memory, double? threshold = null)
            => Task.FromResult<Memory.Memory?>(null);

        public Task<Memory.Memory?> GetByIdAsync(MemoryId id)
            => Task.FromResult(_items.TryGetValue(id, out var m) ? m : null);

        public Task<Memory.Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null)
            => Task.FromResult(_items.TryGetValue(id, out var m) ? m : null);

        public Task<bool> DeleteAsync(MemoryId id)
            => Task.FromResult(_items.Remove(id));

        public Task<IReadOnlyCollection<Memory.Memory>> ListAllAsync()
            => Task.FromResult<IReadOnlyCollection<Memory.Memory>>(_items.Values.ToList());

        public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
            => Task.FromResult<IReadOnlyCollection<MemorySearchResult>>(SearchHits.ToList());

        public Task RebuildIndexAsync() => Task.CompletedTask;

        public IReadOnlyCollection<Memory.Memory> All => _items.Values.ToList();
    }

    private static (ScopedMemoryService Service, FakeMemoryService Project, FakeMemoryService Local, FakeMemoryService Global) Build()
    {
        var project = new FakeMemoryService();
        var local = new FakeMemoryService();
        var global = new FakeMemoryService();
        var service = new ScopedMemoryService(
            [new ProjectLevel(new ProjectScope(ProjectRoot), project)],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ProjectRoot,
            localService: local,
            canonicalRoot: CanonicalRoot);
        return (service, project, local, global);
    }

    private static Memory.Memory NewMemory(string content) => new(MemoryType.Fact, content);

    [Fact]
    public async Task Save_ExplicitProjectLocal_LandsInLocalOnly()
    {
        var (service, project, local, global) = Build();

        var result = await service.SaveAsync(NewMemory("local secret"), "project-local");

        Assert.Equal(MemoryScopeKind.ProjectLocal, result.Scoped.Scope);
        Assert.Single(local.All);
        Assert.Empty(project.All);
        Assert.Empty(global.All);
    }

    [Fact]
    public async Task Save_Default_StillProject()
    {
        var (service, project, local, _) = Build();

        await service.SaveAsync(NewMemory("shared note"));

        Assert.Single(project.All);
        Assert.Empty(local.All);
    }

    [Fact]
    public async Task List_Merged_OrdersLocalProjectGlobal()
    {
        var (service, _, _, _) = Build();
        await service.SaveAsync(NewMemory("g"), "global");
        await service.SaveAsync(NewMemory("p"), "project");
        await service.SaveAsync(NewMemory("l"), "project-local");

        var merged = (await service.ListAsync("merged")).ToList();

        Assert.Equal(
            [MemoryScopeKind.ProjectLocal, MemoryScopeKind.Project, MemoryScopeKind.Global],
            merged.Select(m => m.Scope).ToList());
    }

    [Fact]
    public async Task List_Merged_SameUlidKeepsLocal()
    {
        var (service, project, local, _) = Build();
        var id = MemoryId.NewId();
        await project.SaveAsync(new Memory.Memory(MemoryType.Fact, "project copy", id: id));
        await local.SaveAsync(new Memory.Memory(MemoryType.Fact, "local copy", id: id));

        var merged = (await service.ListAsync("merged")).ToList();

        var single = Assert.Single(merged);
        Assert.Equal(MemoryScopeKind.ProjectLocal, single.Scope);
        Assert.Equal("local copy", single.Memory.Content);
    }

    [Fact]
    public async Task GetDelete_ProjectLocal_RoundTrip()
    {
        var (service, _, _, _) = Build();
        var saved = await service.SaveAsync(NewMemory("roundtrip"), "project-local");

        var fetched = await service.GetByIdAsync(MemoryReference.ForProjectLocal(saved.Scoped.Memory.Id, CanonicalRoot));
        Assert.NotNull(fetched);
        Assert.Equal(MemoryScopeKind.ProjectLocal, fetched.Scope);

        Assert.True(await service.DeleteAsync(MemoryReference.ForProjectLocal(saved.Scoped.Memory.Id, CanonicalRoot)));
        Assert.Null(await service.GetByIdAsync(MemoryReference.ForProjectLocal(saved.Scoped.Memory.Id, CanonicalRoot)));
    }

    [Fact]
    public async Task UnwiredLocal_SaveThrows_ReadsEmpty()
    {
        var project = new FakeMemoryService();
        var global = new FakeMemoryService();
        var service = new ScopedMemoryService(
            [new ProjectLevel(new ProjectScope(ProjectRoot), project)],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ProjectRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(NewMemory("x"), "project-local"));
        Assert.Empty(await service.ListAsync("project-local"));
        Assert.Null(await service.GetByIdAsync(MemoryId.NewId(), "project-local"));
    }

    [Fact]
    public async Task TwoServices_DoNotLeakLocal()
    {
        var (serviceA, _, _, _) = Build();
        var (serviceB, _, localB, _) = Build();
        await serviceA.SaveAsync(NewMemory("a secret"), "project-local");

        Assert.Empty(localB.All);
        Assert.Empty(await serviceB.ListAsync("project-local"));
    }

    [Fact]
    public async Task MoveToLocal_MovesProjectMemory()
    {
        var (service, project, local, _) = Build();
        var saved = await service.SaveAsync(NewMemory("movable"), "project");

        var moved = await service.MoveToLocalAsync(MemoryReference.ForProject(saved.Scoped.Memory.Id, ProjectRoot));

        Assert.NotNull(moved);
        Assert.Equal(MemoryScopeKind.ProjectLocal, moved.Scope);
        Assert.Empty(project.All);
        Assert.Single(local.All);
    }

    [Fact]
    public async Task MoveToProject_MovesLocalMemory()
    {
        var (service, project, local, _) = Build();
        var saved = await service.SaveAsync(NewMemory("unhide"), "project-local");

        var moved = await service.MoveToProjectAsync(
            MemoryReference.ForProjectLocal(saved.Scoped.Memory.Id, CanonicalRoot), ProjectRoot);

        Assert.NotNull(moved);
        Assert.Equal(MemoryScopeKind.Project, moved.Scope);
        Assert.Empty(local.All);
        Assert.Single(project.All);
    }

    [Fact]
    public async Task CopyToGlobal_FromLocalSource()
    {
        var (service, _, _, global) = Build();
        var saved = await service.SaveAsync(NewMemory("promotable"), "project-local");

        var copied = await service.CopyToGlobalAsync(
            MemoryReference.ForProjectLocal(saved.Scoped.Memory.Id, CanonicalRoot));

        Assert.NotNull(copied);
        Assert.Equal(MemoryScopeKind.Global, copied.Scope);
        Assert.Single(global.All);
    }

    [Fact]
    public async Task MoveToLocal_UnwiredLocal_Throws()
    {
        var project = new FakeMemoryService();
        var global = new FakeMemoryService();
        var service = new ScopedMemoryService(
            [new ProjectLevel(new ProjectScope(ProjectRoot), project)],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ProjectRoot);
        var saved = await service.SaveAsync(NewMemory("stuck"), "project");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.MoveToLocalAsync(MemoryReference.ForProject(saved.Scoped.Memory.Id, ProjectRoot)));
    }

    [Fact]
    public async Task Move_PreservesIdAndCreatedAt()
    {
        var (service, _, _, _) = Build();
        var saved = await service.SaveAsync(NewMemory("identity"), "project");
        var before = saved.Scoped.Memory.CreatedAt;
        Assert.True(before <= DateTimeOffset.UtcNow);

        var moved = await service.MoveToLocalAsync(
            MemoryReference.ForProject(saved.Scoped.Memory.Id, ProjectRoot));

        Assert.NotNull(moved);
        Assert.Equal(saved.Scoped.Memory.Id, moved.Memory.Id);
        Assert.Equal(before, moved.Memory.CreatedAt);
        Assert.True(moved.Memory.UpdatedAt >= before);
    }
}
