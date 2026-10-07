using Eling.Backend;
using Eling.Backend.Services;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The memory scope catalog resolves scopes from the <c>.eling</c> chain on
/// disk plus the workspace catalogue. The case that separates the two: a
/// workspace with a codebase index but no <c>.eling</c> is not a memory scope and
/// must not be offered as one.
/// </summary>
public sealed class ProjectScopeCatalogTests : IDisposable
{
    private readonly string _root;

    public ProjectScopeCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-scopes-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>A directory with a .eling, so it is a scope.</summary>
    private string NewScopeDir(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.Combine(path, ProjectScope.DataDirectoryName));
        return path;
    }

    /// <summary>A plain directory: a workspace, but not a scope.</summary>
    private string NewPlainDir(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static SqliteWorkspacesRegistry NewStore(string name)
        => new(Path.Combine(Path.GetTempPath(), "eling-ws-" + Guid.NewGuid().ToString("N")[..8], name));

    private static RuntimeRegistry RegistryFor(SqliteWorkspacesRegistry store)
        => new(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(null),
            new MemoryChangeBroadcaster(),
            store);

    [Fact]
    public async Task ListsOwnScopeAndAncestorsFromDiskWithNoRegistry()
    {
        var ancestor = NewScopeDir("repo");
        var own = NewScopeDir(Path.Combine("repo", "service"));
        using var store = NewStore("chain.db");

        // Nothing registered: everything the caller sees must come from walking
        // the .eling chain.
        var result = await new ProjectScopeCatalog(RegistryFor(store)).ListAsync(own);

        Assert.Equal(Path.GetFullPath(own), result.Cwd);
        Assert.Collection(
            result.Scopes,
            first =>
            {
                Assert.Equal(Path.GetFullPath(own), first.Root);
                Assert.True(first.Writable);
            },
            second => Assert.Equal(Path.GetFullPath(ancestor), second.Root));
    }

    [Fact]
    public async Task AncestorIsListedAndWritableOnlyWithOwnScope()
    {
        var ancestor = NewScopeDir("repo");
        var own = NewScopeDir(Path.Combine("repo", "service"));
        using var store = NewStore("writable.db");
        var catalog = new ProjectScopeCatalog(RegistryFor(store));

        // A workspace with its own scope can write to every level of its chain.
        var withOwn = await catalog.ListAsync(own);
        Assert.All(withOwn.Scopes, s => Assert.True(s.Writable));

        // Without one, the chain head is not a write target — the rule
        // ResolveAncestorProjectRoot enforces on the write path.
        var withoutOwn = await catalog.ListAsync(Path.Combine(ancestor, "loose"));
        Assert.All(withoutOwn.Scopes, s => Assert.False(s.Writable));
    }

    /// <summary>
    /// The id is a ULID primary key, but the unique constraint on the folder is
    /// the upsert's conflict target — so re-recording a workspace keeps the id it
    /// already had instead of minting a second one for the same folder.
    /// </summary>
    [Fact]
    public async Task ReregisteringAWorkspaceKeepsItsId()
    {
        var own = NewScopeDir("repo");
        using var store = NewStore("stableid.db");
        var registry = RegistryFor(store);
        var now = DateTimeOffset.UtcNow;

        registry.RecordWorkspacesForTest([new RegisteredWorkspace(string.Empty, own, own, true, now, now)]);
        var first = (await store.ListAsync()).Single();

        for (var i = 0; i < 3; i++)
        {
            registry.RecordWorkspacesForTest([
                new RegisteredWorkspace(string.Empty, own, own, true, now, DateTimeOffset.UtcNow)
            ]);
        }

        var rows = await store.ListAsync();
        Assert.Single(rows);
        Assert.Equal(first.WorkspaceId, rows[0].WorkspaceId);
        Assert.True(System.Ulid.TryParse(rows[0].WorkspaceId, out _));
        // The first-seen date is history, not something a re-registration rewrites.
        Assert.Equal(now, rows[0].FirstSeenAt);
        Assert.True(rows[0].LastSeenAt > now);
    }

    /// <summary>
    /// The catalogue records folders. One becomes a memory scope only when it
    /// actually holds a <c>.eling</c>, so a remembered codebase-only workspace
    /// must not appear in the memory picker.
    /// </summary>
    [Fact]
    public async Task RememberedWorkspaceWithoutDotEling_IsNotAScope()
    {
        var own = NewScopeDir("repo");
        var indexOnly = NewPlainDir("indexed-only");
        using var store = NewStore("indexonly.db");
        var registry = RegistryFor(store);

        var now = DateTimeOffset.UtcNow;
        registry.RecordWorkspacesForTest([
            new RegisteredWorkspace(string.Empty, own, own, true, now, now),
            new RegisteredWorkspace(string.Empty, indexOnly, indexOnly, true, now, now),
        ]);

        var scopes = await new ProjectScopeCatalog(registry).ListAsync(own);

        Assert.Contains(scopes.Scopes, s => s.Root == Path.GetFullPath(own));
        Assert.DoesNotContain(scopes.Scopes, s => s.Root == Path.GetFullPath(indexOnly));
    }

    /// <summary>
    /// A remembered workspace that still holds a <c>.eling</c> is a scope, and
    /// shows up as <c>registered</c> once this workspace's own chain is placed.
    /// </summary>
    [Fact]
    public async Task RememberedWorkspaceWithDotEling_JoinsAsRegistered()
    {
        var own = NewScopeDir("repo");
        var elsewhere = NewScopeDir("other");
        using var store = NewStore("registered.db");
        var registry = RegistryFor(store);

        var now = DateTimeOffset.UtcNow;
        registry.RecordWorkspacesForTest([new RegisteredWorkspace(string.Empty, elsewhere, elsewhere, true, now, now)]);

        var scopes = await new ProjectScopeCatalog(registry).ListAsync(own);

        var registered = scopes.Scopes.Single(s => s.Root == Path.GetFullPath(elsewhere));
        Assert.False(registered.Writable);

        // The chain still leads, so the nearest scope is the first entry.
        Assert.Equal(Path.GetFullPath(own), scopes.Scopes.First().Root);
    }

    [Fact]
    public async Task MemoryIsReadableForAScopeWithNoLiveRuntime()
    {
        var own = NewScopeDir("repo");
        using var store = NewStore("disk.db");
        var registry = RegistryFor(store);

        // Nothing is alive, but the scope exists on disk — the case that used to
        // return null and hide every memory written there.
        var service = registry.TryResolveMemoryServiceByScopeRoot(own);
        Assert.NotNull(service);
        Assert.Empty(await service.ListAllAsync());

        // An uninitialized directory is still not a scope.
        Assert.Null(registry.TryResolveMemoryServiceByScopeRoot(Path.Combine(_root, "missing")));
    }
}