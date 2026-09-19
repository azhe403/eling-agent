using Eling.Core.Exceptions;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Xunit.Abstractions;

namespace Eling.Core.Tests;

/// <summary>
/// Chain-aware ScopedMemoryService: the head level is the write target, merged
/// reads fan out over every level, and an empty chain blocks project saves.
/// </summary>
public sealed class ScopedMemoryServiceChainTests
{
    private const string ChildRoot = @"C:\work\acme\integrations\payments";
    private const string ParentRoot = @"C:\work\acme\integrations";
    private readonly ITestOutputHelper? _output;

    public ScopedMemoryServiceChainTests(ITestOutputHelper? output = null)
    {
        _output = output;
    }

    private sealed class FakeMemoryService : IMemoryService
    {
        private readonly Dictionary<MemoryId, Memory.Memory> _items = new();

        public Task<SaveResult> SaveAsync(Memory.Memory memory)
        {
            var match = _items.Values.FirstOrDefault(m =>
                m.Status == MemoryStatus.Active &&
                m.Type == memory.Type &&
                (string.Equals(m.Content.Trim(), memory.Content.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 MemorySimilarity.CalculateSimilarity(m.Content, memory.Content) >= 0.60));

            if (match != null)
            {
                var merged = new Memory.Memory(
                    match.Type,
                    memory.Content,
                    Memory.Memory.NormalizeTags(match.Tags.Concat(memory.Tags)),
                    memory.Source ?? match.Source,
                    match.Status,
                    match.Id,
                    match.CreatedAt,
                    DateTimeOffset.UtcNow);
                _items[match.Id] = merged;
                return Task.FromResult(new SaveResult(merged, SaveAction.Updated, match));
            }

            _items[memory.Id] = memory;
            return Task.FromResult(new SaveResult(memory, SaveAction.Created));
        }

        public Task<Memory.Memory?> FindActiveSimilarAsync(Memory.Memory memory, double? threshold = null)
        {
            var limit = threshold ?? 0.60;
            var match = _items.Values.FirstOrDefault(m =>
                m.Status == MemoryStatus.Active &&
                m.Type == memory.Type &&
                (string.Equals(m.Content.Trim(), memory.Content.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 MemorySimilarity.CalculateSimilarity(m.Content, memory.Content) >= limit));
            return Task.FromResult(match);
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
    public async Task SaveAsync_ProjectScope_AncestorOnly_Throws()
    {
        var global = new FakeMemoryService();
        var parent = new FakeMemoryService();
        var service = new ScopedMemoryService(
            [new ProjectLevel(new ProjectScope(ParentRoot), parent)],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ChildRoot);

        await Assert.ThrowsAsync<ProjectScopeNotInitializedException>(
            () => service.SaveAsync(NewMemory("x"), "project"));
        Assert.True(service.IsInitialized);
        Assert.False(service.HasOwnScope);
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
    public void ResolveAncestorProjectRoot_WithoutOwnScope_ResolvesParentIfLevelExists()
    {
        var service = new ScopedMemoryService(
            [new ProjectLevel(new ProjectScope(ParentRoot), new FakeMemoryService())],
            new FakeMemoryService(),
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ChildRoot);

        var root = service.ResolveAncestorProjectRoot("integrations");
        Assert.Equal(ParentRoot, root);
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

    [Fact]
    public async Task SaveAsync_CrossScope_MatchesExistingInGlobal_UpdatesGlobalWithoutDuplicatingToProject()
    {
        var (service, child, parent, global) = Build();

        var existingGlobal = new Memory.Memory(MemoryType.Preference, "Always check git hygiene before commit", ["hygiene", "git"]);
        await global.SaveAsync(existingGlobal);

        var incoming = new Memory.Memory(MemoryType.Preference, "Always check git hygiene and status before commit", ["workflow"]);
        var result = await service.SaveAsync(incoming, "project");

        Assert.Equal(SaveAction.Updated, result.Action);
        Assert.Equal(MemoryScopeKind.Global, result.Scope);
        Assert.Null(result.ProjectRoot);
        Assert.Equal(existingGlobal.Id, result.Id);
        Assert.Contains("workflow", result.Memory.Tags);
        Assert.Contains("hygiene", result.Memory.Tags);

        Assert.Empty(child.All);
        Assert.Empty(parent.All);
        Assert.Single(global.All);
    }

    [Fact]
    public async Task SaveAsync_CrossScope_MatchesExistingInAncestor_UpdatesAncestorWithoutDuplicatingToChild()
    {
        var (service, child, parent, global) = Build();

        var existingAncestor = new Memory.Memory(MemoryType.Decision, "Architecture decision on API contracts", ["api"]);
        await parent.SaveAsync(existingAncestor);

        var incoming = new Memory.Memory(MemoryType.Decision, "Architecture decision on API contracts and schemas", ["schema"]);
        var result = await service.SaveAsync(incoming, "project");

        Assert.Equal(SaveAction.Updated, result.Action);
        Assert.Equal(MemoryScopeKind.Project, result.Scope);
        Assert.Equal(ParentRoot, result.ProjectRoot);
        Assert.Equal(existingAncestor.Id, result.Id);
        Assert.Contains("schema", result.Memory.Tags);

        Assert.Empty(child.All);
        Assert.Single(parent.All);
        Assert.Empty(global.All);
    }

    [Fact]
    public async Task SaveToProjectAsync_ExplicitTarget_WritesToTargetAndHintsChildDuplicate()
    {
        var (service, child, parent, _) = Build();

        var childMem = new Memory.Memory(MemoryType.Preference, "Selalu gunakan conventional commit di semua repo", ["git"]);
        await child.SaveAsync(childMem);

        var incoming = new Memory.Memory(MemoryType.Preference, "Selalu gunakan conventional commit di semua repo dengan delay", ["workflow"]);
        var result = await service.SaveToProjectAsync(incoming, ParentRoot);

        Assert.Equal(ParentRoot, result.ProjectRoot);
        Assert.Single(parent.All);
        Assert.Single(child.All);
        Assert.Contains("consider promote/move", result.Reason);
    }

    [Fact]
    public async Task Simulate_CrossScope_FullWorkflow()
    {
        var (service, child, parent, global) = Build();

        var globalRule = new Memory.Memory(MemoryType.Preference, "Selalu lakukan git hygiene check sebelum commit", ["hygiene", "git"]);
        await global.SaveAsync(globalRule);
        _output?.WriteLine($"[Step 1] Initialized Global Memory: ID={globalRule.Id}, Content='{globalRule.Content}'");

        var ancestorDecision = new Memory.Memory(MemoryType.Decision, "Gunakan REST JSON API untuk integrasi modul internal", ["api", "architecture"]);
        await parent.SaveAsync(ancestorDecision);
        _output?.WriteLine($"[Step 2] Initialized Ancestor Memory: ID={ancestorDecision.Id}, Content='{ancestorDecision.Content}'");

        var incomingHygiene = new Memory.Memory(MemoryType.Preference, "Selalu jalankan git hygiene check dan status sebelum commit", ["workflow"]);
        var res1 = await service.SaveAsync(incomingHygiene, "project");
        _output?.WriteLine($"\n[Step 3] Child calls SaveAsync for Git Hygiene (target=project):");
        _output?.WriteLine($"         Action={res1.Action}, Scope={res1.Scope}, TargetRoot={res1.ProjectRoot ?? "(global)"}, Reason='{res1.Reason}'");
        Assert.Equal(SaveAction.Updated, res1.Action);
        Assert.Equal(MemoryScopeKind.Global, res1.Scope);
        Assert.Equal(globalRule.Id, res1.Id);
        Assert.Empty(child.All);

        var incomingApi = new Memory.Memory(MemoryType.Decision, "Gunakan REST JSON API untuk integrasi modul internal dan external gateway", ["gateway"]);
        var res2 = await service.SaveAsync(incomingApi, "project");
        _output?.WriteLine($"\n[Step 4] Child calls SaveAsync for API Decision (target=project):");
        _output?.WriteLine($"         Action={res2.Action}, Scope={res2.Scope}, TargetRoot={res2.ProjectRoot}, Reason='{res2.Reason}'");
        Assert.Equal(SaveAction.Updated, res2.Action);
        Assert.Equal(MemoryScopeKind.Project, res2.Scope);
        Assert.Equal(ParentRoot, res2.ProjectRoot);
        Assert.Equal(ancestorDecision.Id, res2.Id);
        Assert.Empty(child.All);

        var childSpecific = new Memory.Memory(MemoryType.Fact, "Modul payments menggunakan Stripe SDK v3 untuk proses checkout", ["stripe", "payments"]);
        var res3 = await service.SaveAsync(childSpecific, "project");
        _output?.WriteLine($"\n[Step 5] Child calls SaveAsync for Child-Specific Payment Rule (target=project):");
        _output?.WriteLine($"         Action={res3.Action}, Scope={res3.Scope}, TargetRoot={res3.ProjectRoot}, Reason='{res3.Reason}'");
        Assert.Equal(SaveAction.Created, res3.Action);
        Assert.Equal(MemoryScopeKind.Project, res3.Scope);
        Assert.Equal(ChildRoot, res3.ProjectRoot);
        Assert.Single(child.All);
    }
}