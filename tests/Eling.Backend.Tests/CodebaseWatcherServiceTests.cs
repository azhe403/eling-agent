using Eling.Backend.Bootstrap;
using Eling.Core.Codebase;
using Eling.Core.Scope;
using Xunit.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// The watcher is the default indexing path now that its opt-out flag is
/// gone, so these tests pin the behaviours that matter: it still activates
/// when that removed flag carries its old opt-out value, a drained batch
/// reaches the index, and the enqueue filter drops what a full pass would skip.
/// <para>
/// Waits poll to a deadline instead of sleeping a fixed span. FileSystemWatcher
/// delivery and the service's 900ms debounce are real time, so the assertion
/// is "the batch eventually drained" — a slow machine widens the window
/// rather than failing the run.
/// </para>
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseWatcherServiceTests : IDisposable
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    /// <summary>
    /// Removed from the service. Set to its documented opt-out value so the
    /// removal itself stays verifiable.
    /// </summary>
    private const string LegacyWatcherFlagEnv = "ELING_CODEBASE_WATCHER";

    /// <summary>
    /// Upper bound on waiting for watcher-driven activity: activation, and a
    /// batch draining into the index. Both are real time.
    /// </summary>
    private static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Upper bound on the stop handshake, so a wedged loop cannot hang the run.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ITestOutputHelper _output;
    private readonly TestOutputLogger<CodebaseWatcherService> _logger;
    private readonly string _workspace;
    private readonly string _dataDir;
    private readonly string? _originalDataDir;
    private readonly string? _originalLegacyFlag;
    private readonly CodebaseIndexService _index;
    private readonly CodebaseWatcherService _watcher;

    public CodebaseWatcherServiceTests(ITestOutputHelper output)
    {
        _output = output;

        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-watcher-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-watcher-data-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);
        _originalLegacyFlag = Environment.GetEnvironmentVariable(LegacyWatcherFlagEnv);
        Environment.SetEnvironmentVariable(LegacyWatcherFlagEnv, null);

        // Rooted at an isolated data dir so the index DB never lands in the
        // real Eling store, and the workspace stays free of stray files that
        // would change the expected file counts. The watcher gets a real
        // logger, not NullLogger: a batch-index failure is swallowed inside the
        // service, so without this it would leave no trace at all.
        _index = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
        _logger = new TestOutputLogger<CodebaseWatcherService>(output);
        _watcher = new CodebaseWatcherService(_index, _logger);
    }

    [Fact]
    public async Task StartAsync_ActivatesTheWatcher_EvenWithTheRemovedOptOutFlag()
    {
        // The flag is gone from the service, so even its old opt-out value
        // cannot keep the watcher off. Asserted here rather than in a separate
        // test because on its own it proved nothing this does not.
        Environment.SetEnvironmentVariable(LegacyWatcherFlagEnv, "0");

        await WithActiveWatcherAsync(async () => Assert.True(_watcher.IsActive));
    }

    [Fact]
    public async Task StartAsync_WithUninitializedIndex_DoesNotRunStartupCatchUp()
    {
        // A file exists before the watcher starts, but the index is uninitialized (0 files).
        File.WriteAllText(Path.Combine(_workspace, "existing.cs"), "class Existing { }");

        await WithActiveWatcherAsync(async () =>
        {
            // Give any potential startup task a moment to attempt indexing if it were running
            await Task.Delay(200);
            Assert.Equal(0, await IndexedFileCountAsync());
        });
    }

    [Fact]
    public async Task StartAsync_WithExistingIndex_RunsStartupCatchUp()
    {
        // Existing file is indexed first so the index is initialized (FileCount > 0).
        File.WriteAllText(Path.Combine(_workspace, "initial.cs"), "class Initial { }");
        await _index.IndexAsync();
        Assert.Equal(1, await IndexedFileCountAsync());

        // A second file is added while the watcher is stopped.
        File.WriteAllText(Path.Combine(_workspace, "catchup.cs"), "class CatchUp { }");

        await WithActiveWatcherAsync(async () =>
        {
            await PollUntilAsync(
                async () => await IndexedFileCountAsync() == 2,
                "the startup catch-up pass to index the new file (FileCount == 2)");

            Assert.Equal(2, await IndexedFileCountAsync());
        });
    }

    [Fact]
    public async Task Drain_IndexesAFileCreatedAfterTheWatcherStarted()
    {
        // Seed the index so it is initialized (FileCount > 0)
        File.WriteAllText(Path.Combine(_workspace, "seed.cs"), "class Seed { }");
        await _index.IndexAsync();

        await WithActiveWatcherAsync(async () =>
        {
            File.WriteAllText(Path.Combine(_workspace, "watched.cs"), "class Watched { }");

            await PollUntilAsync(
                async () => await IndexedFileCountAsync() >= 2,
                "the created file to reach the index (FileCount >= 2)");

            Assert.Equal(2, await IndexedFileCountAsync());
        });
    }

    [Fact]
    public async Task Drain_WhenIndexUninitialized_DropsEventsWithoutIndexing()
    {
        // Index is uninitialized (0 files).
        await WithActiveWatcherAsync(async () =>
        {
            File.WriteAllText(Path.Combine(_workspace, "unwatched.cs"), "class Unwatched { }");

            // Give debounce and drain loop time to run if it were active
            await Task.Delay(1200);

            Assert.Equal(0, await IndexedFileCountAsync());
        });
    }

    [Fact]
    public async Task Drain_IndexesTheOrdinaryFileButNotTheExcludedOne()
    {
        File.WriteAllText(Path.Combine(_workspace, "seed.cs"), "class Seed { }");
        await _index.IndexAsync();

        await WithActiveWatcherAsync(async () =>
        {
            var vendored = Path.Combine(_workspace, "node_modules", "dep");
            Directory.CreateDirectory(vendored);
            File.WriteAllText(Path.Combine(vendored, "dep.txt"), "vendored dependency source");
            File.WriteAllText(Path.Combine(_workspace, "app.cs"), "class App { }");

            // The ordinary file is the positive signal that a batch really
            // drained, so the count of exactly 2 (seed + app) proves the enqueue filter
            // rather than a watcher that silently never fired.
            await PollUntilAsync(
                async () => await IndexedFileCountAsync() >= 2,
                "the ordinary file to reach the index (FileCount >= 2)");

            Assert.Equal(2, await IndexedFileCountAsync());
        });
    }

    /// <summary>
    /// Starts the watcher, waits until it is live, runs the body, then stops it
    /// and checks that it really stopped. StartAsync sits inside the try: if it
    /// throws after ExecuteAsync has built the FileSystemWatcher, the finally is
    /// the only thing left that still releases the handle.
    /// </summary>
    private async Task WithActiveWatcherAsync(Func<Task> body)
    {
        var stopped = false;
        try
        {
            await StartDetachedAsync();
            await PollUntilAsync(
                () => Task.FromResult(_watcher.IsActive),
                "the watcher to activate");

            await body();
        }
        finally
        {
            stopped = await StopQuietlyAsync();
        }

        // Deliberately outside the finally. A throwing finally discards whatever
        // the body threw, so asserting here-instead would let "watcher still
        // active" replace the actionable failure. Here a body failure propagates
        // untouched and this check is simply skipped.
        Assert.True(
            stopped,
            "watcher still active after StopAsync — its FileSystemWatcher would outlive the "
            + "test and the temp workspace could not be deleted on Windows");
    }

    /// <summary>
    /// Starts the watcher with no ambient SynchronizationContext, so
    /// ExecuteAsync's continuations run on the thread pool.
    /// <para>
    /// Without this the state machine captures xUnit's AsyncTestSyncContext,
    /// which is internal, undocumented, and only pumped while the test method is
    /// awaiting. A stop issued from teardown could then never resume the loop:
    /// it would sit out its timeout and leave a live FileSystemWatcher over a
    /// workspace about to be deleted. Detaching for the length of the call makes
    /// the stop deterministic without depending on that internal. The call is
    /// restored immediately, so the test body's own awaits still use xUnit's
    /// context.
    /// </para>
    /// </summary>
    private async Task StartDetachedAsync()
    {
        var ambient = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            await _watcher.StartAsync(CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(ambient);
        }
    }

    /// <summary>
    /// Stops the watcher and reports whether it actually stopped, without ever
    /// throwing — a fault here must not replace the failure that explains the
    /// test result. The caller turns the bool into an assertion in the normal
    /// path, where it cannot mask anything.
    /// </summary>
    private async Task<bool> StopQuietlyAsync()
    {
        using var stopDeadline = new CancellationTokenSource(StopTimeout);
        try
        {
            await _watcher.StopAsync(stopDeadline.Token);
        }
        catch (Exception ex) when (IsShutdownNoise(ex))
        {
            _output.WriteLine(
                $"watcher shutdown reported {ex.GetType().Name}: {ex.Message} "
                + $"(waited at most {StopTimeout.TotalSeconds:0}s)");
        }

        return !_watcher.IsActive;
    }

    /// <summary>
    /// Failures that are teardown noise rather than a defect. Everything else is
    /// allowed to propagate — this path exists to stop a shutdown fault from
    /// masking the assertion that explains the result, not to hide surprises.
    /// </summary>
    private static bool IsShutdownNoise(Exception ex) =>
        ex is OperationCanceledException or ObjectDisposedException;

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

    private async Task<int> IndexedFileCountAsync() =>
        (await _index.GetStatsAsync(CancellationToken.None)).FileCount;

    public void Dispose()
    {
        // The FileSystemWatcher handle is the one resource that would otherwise
        // outlive the test and block removal of the temp workspace on Windows.
        // CodebaseIndexService is not IDisposable and SqliteCodebaseIndex.Dispose
        // is a no-op, so there is nothing to release on the index side. Logging
        // stops first: the service can still log from a watcher callback while
        // being torn down, and xUnit's output helper throws once the test is no
        // longer active. The stop itself is verified in WithActiveWatcherAsync.
        _logger.Stop();
        _watcher.Dispose();

        if (_originalDataDir is null) Environment.SetEnvironmentVariable(DataDirEnv, null);
        else Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        if (_originalLegacyFlag is null) Environment.SetEnvironmentVariable(LegacyWatcherFlagEnv, null);
        else Environment.SetEnvironmentVariable(LegacyWatcherFlagEnv, _originalLegacyFlag);
        CodebaseRebuildScopeTests.TryDelete(_workspace);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
    }
}
