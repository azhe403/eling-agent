using Eling.Backend.Bootstrap;
using Eling.Core.Codebase;
using Eling.Core.Scope;
using Xunit.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// A linked worktree of a repository that is already indexed builds its own
/// index at startup, without the consent question: the repository was indexed
/// once, on purpose, and the worktree holds the same code. A worktree of a
/// repository that was never indexed still waits for consent, exactly like any
/// other workspace.
/// </summary>
[Collection(WorktreeIndexCollection.Name)]
public sealed class CodebaseWorktreeInheritanceTests : IDisposable
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    /// <summary>Upper bound on waiting for the startup pass: real time, real work.</summary>
    private static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ITestOutputHelper _output;
    private readonly TestOutputLogger<CodebaseWatcherService> _logger;
    private readonly string _root;
    private readonly string _dataDir;
    private readonly string? _originalDataDir;

    public CodebaseWorktreeInheritanceTests(ITestOutputHelper output)
    {
        _output = output;

        var tag = Guid.NewGuid().ToString("N")[..8];
        _root = Path.Combine(Path.GetTempPath(), "eling-worktree-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-worktree-data-" + tag);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        _logger = new TestOutputLogger<CodebaseWatcherService>(output);
    }

    [Fact]
    public async Task StartAsync_WorktreeOfAnIndexedRepository_BuildsTheWorktreeIndex()
    {
        var main = CreateMainRepository();
        await IndexAsync(main);
        var worktree = CreateWorktree(main, "feature");
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "class Feature { }");

        var index = CreateIndexService(worktree);
        var watcher = new CodebaseWatcherService(index, _logger);
        try
        {
            await StartDetachedAsync(watcher);
            await PollUntilAsync(
                async () => await IndexedFileCountAsync(index) > 0,
                "the inherited startup pass to index the worktree");

            // The worktree holds one source file. Its .git pointer file is git's
            // own metadata and never belongs in a code index.
            Assert.Equal(1, await IndexedFileCountAsync(index));
        }
        finally
        {
            await StopQuietlyAsync(watcher);
        }
    }

    [Fact]
    public async Task StartAsync_WorktreeOfAnUnindexedRepository_LeavesTheWorktreeIndexEmpty()
    {
        var main = CreateMainRepository();
        var worktree = CreateWorktree(main, "feature");
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "class Feature { }");

        var index = CreateIndexService(worktree);
        var watcher = new CodebaseWatcherService(index, _logger);
        try
        {
            await StartDetachedAsync(watcher);
            await Task.Delay(1200);

            // No consent was given, and the repository was never indexed, so
            // there is nothing to inherit and the index stays empty.
            Assert.Equal(0, await IndexedFileCountAsync(index));
        }
        finally
        {
            await StopQuietlyAsync(watcher);
        }
    }

    /// <summary>
    /// A main checkout: a <c>.git</c> directory marks it.
    /// </summary>
    private string CreateMainRepository()
    {
        var main = Path.Combine(_root, "main");
        Directory.CreateDirectory(Path.Combine(main, ".git"));
        File.WriteAllText(Path.Combine(main, "main.cs"), "class Main { }");
        return main;
    }

    /// <summary>
    /// A linked worktree of <paramref name="main"/>: a <c>.git</c> file points
    /// at <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>, and that directory's
    /// <c>commondir</c> names the main repository's <c>.git</c>.
    /// </summary>
    private string CreateWorktree(string main, string name)
    {
        var worktree = Path.Combine(_root, "worktrees", name);
        Directory.CreateDirectory(worktree);
        var worktreeGit = Path.Combine(main, ".git", "worktrees", name);
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}");
        return worktree;
    }

    private static CodebaseIndexService CreateIndexService(string root) =>
        new(root, new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(root)));

    private static async Task IndexAsync(string root)
    {
        var service = CreateIndexService(root);
        await service.IndexAsync();
    }

    private static async Task<int> IndexedFileCountAsync(CodebaseIndexService index) =>
        (await index.GetStatsAsync(CancellationToken.None)).FileCount;

    /// <summary>
    /// Starts the watcher with no ambient SynchronizationContext, so
    /// ExecuteAsync's continuations run on the thread pool and the stop
    /// handshake is deterministic.
    /// </summary>
    private static async Task StartDetachedAsync(CodebaseWatcherService watcher)
    {
        var ambient = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            await watcher.StartAsync(CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(ambient);
        }
    }

    /// <summary>
    /// Stops the watcher without ever throwing: a fault here must not replace
    /// the failure that explains the test result.
    /// </summary>
    private async Task StopQuietlyAsync(CodebaseWatcherService watcher)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await watcher.StopAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            _output.WriteLine($"watcher shutdown reported {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task PollUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow + ActivityTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(PollInterval, CancellationToken.None);
        }

        throw new TimeoutException(
            $"Timed out after {ActivityTimeout.TotalSeconds:0}s waiting for {because}.");
    }

    public void Dispose()
    {
        // Logging stops first: the service can still log from a watcher callback
        // while being torn down, and xUnit's output helper throws once the test
        // is no longer active.
        _logger.Stop();

        if (_originalDataDir is null) Environment.SetEnvironmentVariable(DataDirEnv, null);
        else Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        CodebaseRebuildScopeTests.TryDelete(_root);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
    }
}
