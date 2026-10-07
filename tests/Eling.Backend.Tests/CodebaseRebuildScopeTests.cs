using Eling.Backend.Endpoints;
using Eling.Core.Codebase;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// Locks the scope-to-targets rule behind the rebuild button, which is the part
/// the UI depends on: the selector must mean the same thing for reads and for
/// the projects that actually get indexed.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseRebuildScopeTests : IDisposable
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    private readonly string _workspace;
    private readonly string _dataDir;
    private readonly string? _originalDataDir;
    private readonly CodebaseIndexService _local;
    private readonly RuntimeRegistry _registry;
    private readonly SqliteWorkspacesRegistry _projects;

    public CodebaseRebuildScopeTests()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-rebuild-scope-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-rebuild-scope-data-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        // Rooted at an isolated data dir so the assertions below about
        // "has / has not an index DB" never touch the real Eling store.
        _local = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
        // Attached so a federated "all" read can include workspaces whose process
        // has exited — that is the behaviour under test here.
        _projects = new SqliteWorkspacesRegistry(
            ElingPaths.ResolveProjectsDbPath(_dataDir, elingDataDir: _dataDir));
        _registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_dataDir, "user-scope")),
            new MemoryChangeBroadcaster(),
            _projects);
    }

    [Fact]
    public async Task NoParameters_TargetsOnlyThisBackendWorkspace()
    {
        var request = await CodebaseEndpoints.ResolveRebuildRequestAsync(_local, _registry, null, null, false);

        Assert.Equal("project", request.Scope);
        Assert.Equal([_local.ProjectRoot], request.Targets);
        Assert.Empty(request.Skipped);
        Assert.False(request.Full);
    }

    [Fact]
    public async Task NamedProjectWithoutIndex_IsStillTargeted()
    {
        var fresh = Path.Combine(_dataDir, "never-indexed");
        Directory.CreateDirectory(fresh);

        var request = await CodebaseEndpoints.ResolveRebuildRequestAsync(_local, _registry, null, [fresh], false);

        Assert.Equal("projects", request.Scope);
        Assert.Equal([Path.GetFullPath(fresh)], request.Targets);
        Assert.Empty(request.Skipped);
    }

    [Fact]
    public async Task FullFlag_IsCarriedIntoTheRequest()
    {
        var request = await CodebaseEndpoints.ResolveRebuildRequestAsync(_local, _registry, null, [_local.ProjectRoot], true);

        Assert.True(request.Full);
    }

    [Fact]
    public async Task AllScope_SkipsRootsThatHaveNoIndexYet()
    {
        // No runtimes are registered, so "all" resolves to this workspace alone.
        Assert.False(File.Exists(ElingPaths.ResolveCodebaseDbPath(_local.ProjectRoot)));

        var request = await CodebaseEndpoints.ResolveRebuildRequestAsync(_local, _registry, "all", null, false);

        Assert.Equal("all", request.Scope);
        Assert.Empty(request.Targets);
        Assert.Equal([_local.ProjectRoot], request.Skipped);
    }

    /// <summary>
    /// "All projects" must cover every workspace that has an index, including
    /// ones whose process has exited. A codebase index is a read-only file in
    /// the global store and stays readable after its owner is gone, so a
    /// live-only reading understated the store by exactly the closed workspaces a
    /// user is most likely to come back for.
    /// </summary>
    [Fact]
    public async Task AllScope_IncludesWorkspacesWhoseProcessHasExited()
    {
        var closed = Path.Combine(_dataDir, "closed-workspace");
        Directory.CreateDirectory(closed);
        var closedDb = ElingPaths.ResolveCodebaseDbPath(closed);
        Directory.CreateDirectory(Path.GetDirectoryName(closedDb)!);
        File.WriteAllBytes(closedDb, []);

        // The catalogue keeps the folder after its process is gone, which is the
        // whole reason the store exists.
        var now = DateTimeOffset.UtcNow;
        _registry.RecordWorkspacesForTest([new RegisteredWorkspace(string.Empty, closed, closed, true, now, now)]);

        var request = await CodebaseEndpoints.ResolveRebuildRequestAsync(_local, _registry, "all", null, false);

        Assert.Contains(Path.GetFullPath(closed), request.Targets);
    }

    /// <summary>
    /// The exclusion decision is stored at registration, so a workspace that
    /// opted out of indexing stays out of a federated read even though its
    /// folder is still in the catalogue.
    /// </summary>
    [Fact]
    public async Task AllScope_ExcludesWorkspacesThatOptedOut()
    {
        var excluded = Path.Combine(_dataDir, "excluded-workspace");
        Directory.CreateDirectory(excluded);
        var excludedDb = ElingPaths.ResolveCodebaseDbPath(excluded);
        Directory.CreateDirectory(Path.GetDirectoryName(excludedDb)!);
        File.WriteAllBytes(excludedDb, []);

        var now = DateTimeOffset.UtcNow;
        _registry.RecordWorkspacesForTest([
            new RegisteredWorkspace(string.Empty, excluded, excluded, false, now, now)
        ]);

        var request = await CodebaseEndpoints.ResolveRebuildRequestAsync(_local, _registry, "all", null, false);

        Assert.DoesNotContain(Path.GetFullPath(excluded), request.Targets);
    }

    [Fact]
    public async Task AllScope_TargetsRootsThatAlreadyHaveAnIndex()
    {
        // No runtimes are registered, so "all" resolves to this workspace alone.
        // ResolveRebuildRequest only probes for the file's existence, so an empty
        // placeholder is enough — opening a real SQLite index here would just
        // leave a locked handle behind for cleanup to trip over.
        var dbPath = ElingPaths.ResolveCodebaseDbPath(_local.ProjectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllBytes(dbPath, []);

        var request = await CodebaseEndpoints.ResolveRebuildRequestAsync(_local, _registry, "all", null, false);

        Assert.Equal("all", request.Scope);
        Assert.Equal([_local.ProjectRoot], request.Targets);
        Assert.Empty(request.Skipped);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _projects.Dispose();
        if (_originalDataDir is null) Environment.SetEnvironmentVariable(DataDirEnv, null);
        else Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        TryDelete(_workspace);
        TryDelete(_dataDir);
    }

    // Best-effort scratch cleanup. A locked handle (a SQLite pool, an
    // antivirus scan) must not fail an otherwise passing test, and a leftover
    // directory under the temp root is harmless — the OS reclaims it.
    internal static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
