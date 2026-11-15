using Eling.Backend.Mcp.Tools;
using Eling.Core.Codebase;
using Eling.Core.Scope;

namespace Eling.Backend.Tests;

/// <summary>
/// Shared fixture for the two codebase_search match-mode test classes: one
/// temp workspace per test (xUnit constructs the test class anew per test),
/// an isolated ELING_DATA_DIR scratch store, and a helper to index either
/// root. The collection attribute on the derived classes keeps the
/// process-global ELING_DATA_DIR edits sequential against the other
/// data-dir tests.
/// </summary>
public abstract class CodebaseSearchToolTestBase : IDisposable
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    protected readonly string _workspace;
    protected readonly string _dataDir;
    private readonly string? _originalDataDir;

    protected CodebaseSearchToolTestBase()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-match-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-match-data-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);
    }

    protected async Task IndexAsync(string root)
    {
        var svc = new CodebaseIndexService(
            root,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(root)));
        await svc.IndexAsync(full: true);
    }

    protected CodebaseSearchTool Tool()
        => new(new CodebaseIndexService(
                _workspace,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace))),
            registry: null, logger: null);

    protected async Task WriteAsync(string relativePath, string contents)
    {
        var full = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, contents);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        CodebaseRebuildScopeTests.TryDelete(_workspace);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
    }
}
