using Eling.Backend.Mcp.Tools;
using Eling.Backend.Scope;
using Eling.Core;
using Eling.Core.Exceptions;
using Eling.Core.Memory;
using Eling.Core.Scope;

namespace Eling.Backend.Tests;

public sealed class MemoryWriteToolProjectScopeTests : IDisposable
{
    private readonly string _root;
    private DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    public MemoryWriteToolProjectScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-write-scope-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private JsonProjectScopePolicyStore NewStore()
        => new(new UserScope(_root), () => _now, Path.Combine(_root, "home"));

    [Fact]
    public async Task Save_OnDisabledProject_RoutesToGlobalAndFlags()
    {
        var cwd = Path.Combine(_root, "acme");
        var store = NewStore();
        await store.SetProjectAsync(cwd, ProjectScopeDecision.Disabled);
        var scoped = new FakeScopedMemoryService { Cwd = cwd, Initialized = false };

        var response = await new MemoryWriteTool(scoped, policyStore: store).SaveAsync("hello");

        Assert.Equal("global", response.Scope);
        Assert.True(response.ProjectScopeDisabled);
        Assert.False(response.InitRequired);
        Assert.Single(scoped.SavedScopes);
        Assert.Equal("global", scoped.SavedScopes[0]);
    }

    [Fact]
    public async Task Save_OnAskUninitialized_ReturnsInitRequired()
    {
        var cwd = Path.Combine(_root, "acme");
        var scoped = new FakeScopedMemoryService { Cwd = cwd, Initialized = false };

        var response = await new MemoryWriteTool(scoped, policyStore: NewStore()).SaveAsync("hello");

        Assert.True(response.InitRequired);
        Assert.Equal("init-required", response.Action);
        Assert.False(response.ProjectScopeDisabled);
    }

    [Fact]
    public async Task Save_ExplicitGlobal_NotFlagged()
    {
        var cwd = Path.Combine(_root, "acme");
        var store = NewStore();
        await store.SetProjectAsync(cwd, ProjectScopeDecision.Disabled);
        var scoped = new FakeScopedMemoryService { Cwd = cwd, Initialized = false };

        var response = await new MemoryWriteTool(scoped, policyStore: store).SaveAsync("hello", scope: "global");

        Assert.Equal("global", response.Scope);
        Assert.False(response.ProjectScopeDisabled);
    }

    private sealed class FakeScopedMemoryService : IScopedMemoryService
    {
        public string Cwd { get; init; } = "";
        public bool Initialized { get; init; }
        public List<string?> SavedScopes { get; } = [];

        public Task<ScopedSaveResult> SaveAsync(Memory memory, string? scope = null)
        {
            SavedScopes.Add(scope);
            var isGlobal = string.Equals(scope?.Trim(), "global", StringComparison.OrdinalIgnoreCase);
            if (!isGlobal && !Initialized)
            {
                throw new ProjectScopeNotInitializedException(Cwd);
            }

            var kind = isGlobal ? MemoryScopeKind.Global : MemoryScopeKind.Project;
            var saved = new ScopedMemory(memory, kind, isGlobal ? null : Cwd);
            return Task.FromResult(new ScopedSaveResult(saved, SaveAction.Created));
        }

        public IMemoryService ProjectService => null!;
        public IMemoryService GlobalService => null!;
        public string? ProjectRoot => Cwd;
        public IReadOnlyList<string> ChainRoots => [];
        public bool IsInitialized => Initialized;
        public bool HasOwnScope => Initialized;

        public Task RebuildIndexAsync(string? scope = null) => Task.CompletedTask;
        public Task RebuildProjectIndexAsync(string projectRoot) => Task.CompletedTask;

        public string ResolveAncestorProjectRoot(string projectName) => throw new NotImplementedException();
        public Task<ScopedSaveResult> SaveToProjectAsync(Memory memory, string targetProjectRoot) => throw new NotImplementedException();
        public Task<ScopedMemory?> GetByIdAsync(MemoryReference reference) => throw new NotImplementedException();
        public Task<ScopedMemory?> GetByIdAsync(MemoryId id, string? scope) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(MemoryReference reference) => throw new NotImplementedException();
        public Task<IReadOnlyCollection<ScopedMemory>> ListAsync(string? scope = null, MemoryStatus? status = null) => throw new NotImplementedException();
        public Task<IReadOnlyCollection<ScopedSearchResult>> SearchAsync(string query, string? scope = null, int? limit = null) => throw new NotImplementedException();
        public Task<ScopedMemory?> UpdateAsync(MemoryReference reference, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null) => throw new NotImplementedException();
        public Task<ScopedMemory?> CopyToProjectAsync(MemoryReference source, string targetProjectRoot) => throw new NotImplementedException();
        public Task<ScopedMemory?> CopyToGlobalAsync(MemoryReference source) => throw new NotImplementedException();
        public Task<ScopedMemory?> MoveToProjectAsync(MemoryReference source, string targetProjectRoot) => throw new NotImplementedException();
        public Task<ScopedMemory?> MoveToGlobalAsync(MemoryReference source) => throw new NotImplementedException();
        public Task<ScopedMemory?> PromoteToGlobalAsync(MemoryReference source) => throw new NotImplementedException();
    }
}
