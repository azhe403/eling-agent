using Eling.Backend.Bootstrap;
using Eling.Backend.Mcp;
using Eling.Core;
using Eling.Core.Exceptions;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;

namespace Eling.Backend.Tests;

/// <summary>
/// Task 5 DI contract: per-level services are registered from the chain head,
/// an uninitialized chain yields an empty IScopedMemoryService level list, and
/// building the container never creates a `.eling` directory on disk.
/// </summary>
public sealed class ScopeChainDiTests : IDisposable
{
    private readonly string _root;

    public ScopeChainDiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-dummy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task AddElingCoreServices_Chain_RegistersLevelsAndWritesToHead()
    {
        var root = Path.Combine(_root, "root");
        Directory.CreateDirectory(Path.Combine(root, ".eling"));
        var integrations = Path.Combine(root, "integrations");
        Directory.CreateDirectory(Path.Combine(integrations, ".eling"));
        var payments = Path.Combine(root, "integrations", "payments");
        Directory.CreateDirectory(payments);

        var chain = ScopeChain.Discover(payments);
        var userScope = new UserScope(Path.Combine(_root, "user"));

        var services = new ServiceCollection();
        services.AddElingCoreServices(chain, userScope);
        await using var provider = services.BuildServiceProvider();
        var scoped = provider.GetRequiredService<IScopedMemoryService>();

        Assert.True(scoped.IsInitialized);
        Assert.Equal([Path.GetFullPath(integrations), Path.GetFullPath(root)], scoped.ChainRoots);
        Assert.Equal(Path.GetFullPath(payments), scoped.Cwd);

        var saved = await scoped.SaveAsync(new Memory(MemoryType.Fact, "head write"));
        Assert.Equal(MemoryScopeKind.Project, saved.Scope);
        Assert.Equal(Path.GetFullPath(integrations), saved.ProjectRoot);
        Assert.True(File.Exists(Path.Combine(integrations, ".eling", "memories", saved.Id.Value + ".md")));
    }

    [Fact]
    public async Task AddElingCoreServices_UninitializedChain_EmptyLevels_NoDirCreated()
    {
        var fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(fresh);
        var chain = ScopeChain.Discover(fresh);
        var userScope = new UserScope(Path.Combine(_root, "user"));

        var services = new ServiceCollection();
        services.AddElingCoreServices(chain, userScope);
        await using var provider = services.BuildServiceProvider();
        var scoped = provider.GetRequiredService<IScopedMemoryService>();

        Assert.False(scoped.IsInitialized);
        Assert.Empty(scoped.ChainRoots);
        Assert.False(Directory.Exists(Path.Combine(fresh, ".eling")));

        await Assert.ThrowsAsync<ProjectScopeNotInitializedException>(
            () => scoped.SaveAsync(new Memory(MemoryType.Fact, "x"), "project"));
        Assert.False(Directory.Exists(Path.Combine(fresh, ".eling")));
    }
}