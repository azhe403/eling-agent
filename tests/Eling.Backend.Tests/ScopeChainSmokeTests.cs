using Eling.Backend.Dtos;
using Eling.Backend.Mcp;
using Eling.Backend.Mcp.Tools;
using Eling.Core;
using Eling.Core.Intention;
using Eling.Core.Memory;
using Eling.Core.Memory.Serialization;
using Eling.Core.MemoryRecall;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;

namespace Eling.Backend.Tests;

/// <summary>
/// End-to-end smoke of the consent flow through real DI + filesystem storage
/// (Task 9 plan fixture): ancestor adoption without consent, then
/// memory_init_project consent, then own-scope writes. Each step re-discovers
/// the chain from scratch, mirroring per-request DI resolution in production.
/// </summary>
public sealed class ScopeChainSmokeTests : IDisposable
{
    private readonly string _root;

    public ScopeChainSmokeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-dummy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateDir(params string[] segments)
    {
        var path = Path.Combine([_root, ..segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<(ScopedMemoryService Scoped, MemoryWriteTool Write)> BuildAsync(string cwd)
    {
        var services = new ServiceCollection();
        var userRoot = Path.Combine(_root, "user-scope");
        Directory.CreateDirectory(userRoot);
        var userScope = new UserScope(userRoot);
        services.AddElingCoreServices(ScopeChain.Discover(cwd), userScope);
        var provider = services.BuildServiceProvider();
        var scoped = (ScopedMemoryService)provider.GetRequiredService<IScopedMemoryService>();
        var nonScoped = provider.GetRequiredService<IMemoryService>();
        var write = new MemoryWriteTool(nonScoped, scoped, notifier: new NullMemoryChangeNotifier());
        return (scoped, write);
    }

    private async Task<(IScopedMemoryService Scoped, MemoryWriteTool Write)> BuildFromFresh(string cwd)
    {
        var services = new ServiceCollection();
        var userRoot = Path.Combine(_root, "user-scope");
        Directory.CreateDirectory(userRoot);
        var userScope = new UserScope(userRoot);
        services.AddElingCoreServices(ScopeChain.Discover(cwd), userScope);
        var provider = services.BuildServiceProvider();
        var scoped = provider.GetRequiredService<IScopedMemoryService>();
        var nonScoped = provider.GetRequiredService<IMemoryService>();
        var write = new MemoryWriteTool(nonScoped, scoped, notifier: new NullMemoryChangeNotifier());
        return (scoped, write);
    }

    [Fact]
    public async Task Smoke_AncestorAdoption_ThenInit_ThenOwnScope()
    {
        // Tree: root/.eling + root/integrations/payments (no own .eling)
        var root = CreateDir("root");
        Directory.CreateDirectory(Path.Combine(root, ".eling"));
        var payments = CreateDir("root", "integrations", "payments");
        var parentName = new DirectoryInfo(root).Name;

        // 1. status in payments: ancestor-scope, adoptable
        var statusTool = new MemoryProjectStatusTool(payments);
        var status = await statusTool.GetStatusAsync();
        Assert.Equal("ancestor-scope", status.Posture);
        Assert.True(status.Adoptable);

        // 2. default save lands in ancestor root/.eling, no consent needed
        var (ancestorScoped, ancestorWrite) = await BuildAsync(payments);
        Assert.Equal(Path.GetFullPath(root), ancestorScoped.ChainRoots.Single());
        var ancestorSave = await ancestorWrite.SaveAsync("parent-root memory");
        Assert.Equal("created", ancestorSave.Action);
        Assert.True(File.Exists(Path.Combine(root, ".eling", "memories", ancestorSave.Id.ToString() + ".md")));

        // 3. init creates own .eling under payments; status now own-scope
        var initTool = new MemoryInitProjectTool(payments);
        var initResult = await initTool.InitAsync();
        Assert.Equal("created", initResult.Status);
        Assert.True(Directory.Exists(Path.Combine(payments, ".eling")));
        var statusAfter = await new MemoryProjectStatusTool(payments).GetStatusAsync();
        Assert.Equal("own-scope", statusAfter.Posture);
        Assert.False(statusAfter.Adoptable);

        // 4. default save now lands in payments/.eling
        var (ownScoped, ownWrite) = await BuildAsync(payments);
        Assert.Equal(Path.GetFullPath(payments), ownScoped.ProjectRoot);
        var ownSave = await ownWrite.SaveAsync("own-scope memory");
        Assert.True(File.Exists(Path.Combine(payments, ".eling", "memories", ownSave.Id.ToString() + ".md")));

        // 5. recall merged contains ancestor memory AND own memory
        var recallService = new MemoryRecallService(ownScoped, new NoopIntentionStorage());
        var recall = await recallService.RecallAsync(new MemoryRecallContext(["memory"], null, null));
        var contents = recall.RecallMemories.Select(h => h.Memory.Content).ToList();
        Assert.Contains("parent-root memory", contents);
        Assert.Contains("own-scope memory", contents);
        var ancestorHit = recall.RecallMemories.First(h => h.Memory.Content == "parent-root memory");
        Assert.Equal(Path.GetFullPath(root), ancestorHit.ProjectRoot);
        var ownHit = recall.RecallMemories.First(h => h.Memory.Content == "own-scope memory");
        Assert.Equal(Path.GetFullPath(payments), ownHit.ProjectRoot);
        var projectOnly = await ownScoped.ListAsync("project");
        Assert.All(projectOnly, m => Assert.Equal(Path.GetFullPath(payments), m.ProjectRoot));
    }

    [Fact]
    public async Task Smoke_FreshEmptyDir_SaveBlocked_ThenInitWorks()
    {
        var fresh = CreateDir("fresh-empty");
        var status = await new MemoryProjectStatusTool(fresh).GetStatusAsync();
        Assert.Equal("uninitialized", status.Posture);

        // save without any .eling: init-required, nothing created
        var (scoped, write) = await BuildFromFresh(fresh);
        Assert.False(scoped.IsInitialized);
        var blocked = await write.SaveAsync("should be blocked");
        Assert.Equal("init-required", blocked.Action);
        Assert.False(Directory.Exists(Path.Combine(fresh, ".eling")));

        // after consent + init, save works and dir exists
        var init = await new MemoryInitProjectTool(fresh).InitAsync();
        Assert.Equal("created", init.Status);
        var (initializedScoped, initializedWrite) = await BuildAsync(fresh);
        var saved = await initializedWrite.SaveAsync("now it works");
        Assert.Equal("created", saved.Action);
        Assert.True(Directory.Exists(Path.Combine(fresh, ".eling", "memories")));
    }

    private sealed class NoopIntentionStorage : IIntentionStorage
    {
        public Task SaveAsync(Intention intention) => Task.CompletedTask;
        public Task<Intention?> GetByIdAsync(MemoryId id) => Task.FromResult<Intention?>(null);
        public Task<bool> DeleteAsync(MemoryId id) => Task.FromResult(true);
        public Task<IReadOnlyCollection<Intention>> ListAllAsync()
            => Task.FromResult<IReadOnlyCollection<Intention>>(Array.Empty<Intention>());
    }
}