using Eling.Backend.Endpoints;
using Eling.Core.Codebase;
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
        _registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_dataDir, "user-scope")));
    }

    [Fact]
    public void NoParameters_TargetsOnlyThisBackendWorkspace()
    {
        var request = CodebaseEndpoints.ResolveRebuildRequest(_local, _registry, null, null, false);

        Assert.Equal("project", request.Scope);
        Assert.Equal([_local.ProjectRoot], request.Targets);
        Assert.Empty(request.Skipped);
        Assert.False(request.Full);
    }

    [Fact]
    public void NamedProjectWithoutIndex_IsStillTargeted()
    {
        var fresh = Path.Combine(_dataDir, "never-indexed");
        Directory.CreateDirectory(fresh);

        var request = CodebaseEndpoints.ResolveRebuildRequest(_local, _registry, null, [fresh], false);

        Assert.Equal("projects", request.Scope);
        Assert.Equal([Path.GetFullPath(fresh)], request.Targets);
        Assert.Empty(request.Skipped);
    }

    [Fact]
    public void FullFlag_IsCarriedIntoTheRequest()
    {
        var request = CodebaseEndpoints.ResolveRebuildRequest(_local, _registry, null, [_local.ProjectRoot], true);

        Assert.True(request.Full);
    }

    [Fact]
    public void AllScope_SkipsRootsThatHaveNoIndexYet()
    {
        // No runtimes are registered, so "all" resolves to this workspace alone.
        Assert.False(File.Exists(ElingPaths.ResolveCodebaseDbPath(_local.ProjectRoot)));

        var request = CodebaseEndpoints.ResolveRebuildRequest(_local, _registry, "all", null, false);

        Assert.Equal("all", request.Scope);
        Assert.Empty(request.Targets);
        Assert.Equal([_local.ProjectRoot], request.Skipped);
    }

    [Fact]
    public void AllScope_TargetsRootsThatAlreadyHaveAnIndex()
    {
        // No runtimes are registered, so "all" resolves to this workspace alone.
        // ResolveRebuildRequest only probes for the file's existence, so an empty
        // placeholder is enough — opening a real SQLite index here would just
        // leave a locked handle behind for cleanup to trip over.
        var dbPath = ElingPaths.ResolveCodebaseDbPath(_local.ProjectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllBytes(dbPath, []);

        var request = CodebaseEndpoints.ResolveRebuildRequest(_local, _registry, "all", null, false);

        Assert.Equal("all", request.Scope);
        Assert.Equal([_local.ProjectRoot], request.Targets);
        Assert.Empty(request.Skipped);
    }

    public void Dispose()
    {
        _registry.Dispose();
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
