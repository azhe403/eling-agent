using System.Collections.Concurrent;
using System.Diagnostics;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Debounced background file watcher that keeps the codebase index fresh.
/// Registered on the dashboard owner of a real, non-excluded project
/// session, so it runs by default and there is no opt-out flag: the
/// registration gate in <c>DashboardServices</c> is the only control.
/// File events are batched for 900ms and applied as a scoped incremental
/// index pass. Paths the indexer never covers (excluded dirs, outside the
/// root) are dropped at enqueue time so build output never pollutes the
/// index. A watcher buffer overflow triggers a full incremental rescan.
/// Failures are logged and never crash the host or block search.
/// </summary>
public sealed class CodebaseWatcherService : BackgroundService
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(900);

    private readonly CodebaseIndexService _svc;
    private readonly IMemoryChangeNotifier _notifier;
    private readonly ILogger<CodebaseWatcherService>? _logger;
    private readonly ConcurrentQueue<string> _queue = new();
    private GitignoreFilter? _ignores;
    private FileSystemWatcher? _watcher;
    private int _fullRescanRequested;

    // Published by the ExecuteAsync thread and read from other threads (the
    // status tool, the HTTP endpoint, tests). volatile is the cheap way to
    // declare that intent: it stops the JIT hoisting a read out of a polling
    // loop or caching it in a register. Worth being precise about why it is
    // not strictly required today — await is already a full memory barrier, so
    // a reader that awaits between reads, which is what every current caller
    // does, would be correct without it. It is here so a future tight polling
    // loop is too. A single-flag publish needs no Interlocked.
    private volatile bool _isActive;

    /// <summary>True while the watcher is actively raising events.</summary>
    public bool IsActive => _isActive;

    public CodebaseWatcherService(
        CodebaseIndexService svc,
        ILogger<CodebaseWatcherService>? logger = null,
        IMemoryChangeNotifier? notifier = null)
    {
        _svc = svc;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = _svc.ProjectRoot;
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            _watcher.Created += (_, e) => Enqueue(e.FullPath, "created");
            _watcher.Changed += (_, e) => Enqueue(e.FullPath, "changed");
            _watcher.Deleted += (_, e) => Enqueue(e.FullPath, "deleted");
            _watcher.Renamed += (_, e) =>
            {
                Enqueue(e.OldFullPath, "renamed-from");
                Enqueue(e.FullPath, "renamed-to");
            };
            _watcher.Error += (_, e) =>
            {
                _logger?.LogWarning(e.GetException(),
                    "Codebase watcher buffer overflow; triggering full incremental rescan.");
                Interlocked.Exchange(ref _fullRescanRequested, 1);
            };
            _watcher.EnableRaisingEvents = true;
            _isActive = true;
            _logger?.LogInformation("Codebase watcher active on {Root}.", root);
            await LogIndexStateAtStartupAsync(stoppingToken);
            await RunFullIncrementalPassAsync("startup catch-up", stoppingToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Codebase watcher failed to start on {Root}.", root);
            return;
        }

        using var timer = new PeriodicTimer(DebounceWindow);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await DrainAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down; watcher stops with it.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Codebase watcher loop failed.");
        }
        finally
        {
            _isActive = false;
            _watcher?.Dispose();
            _watcher = null;
            _logger?.LogInformation("Codebase watcher stopped on {Root}.", root);
        }
    }

    /// <summary>
    /// Reports what the local index already holds when the watcher comes up.
    /// Nothing indexes until a file event arrives, so an index that is empty
    /// or was left schema-less by an earlier run stays that way for as long as
    /// the workspace is idle — this line is where that becomes visible instead
    /// of surfacing much later as a failed status read. Never throws: a failure
    /// here must not reach the start-up catch below and take the watcher down.
    /// </summary>
    private async Task LogIndexStateAtStartupAsync(CancellationToken ct)
    {
        try
        {
            var stats = await _svc.GetStatsAsync(ct);
            _logger?.LogInformation(
                "Codebase index state at watcher startup: files={Files} chunks={Chunks} lastIndexedAt={LastIndexedAt} dbPath={DbPath}.",
                stats.FileCount, stats.ChunkCount, stats.LastIndexedAt ?? "never", stats.DbPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex,
                "Codebase index state unreadable at watcher startup; the index needs a rebuild.");
        }
    }

    private static bool IsGitignoreName(string rel) =>
        Path.GetFileName(rel.Replace('\\', '/')).Equals(".gitignore", StringComparison.Ordinal);

    private void Enqueue(string fullPath, string changeType = "changed")
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        string rel;
        try
        {
            rel = Path.GetRelativePath(_svc.ProjectRoot, fullPath).Replace('\\', '/');
        }
        catch (ArgumentException ex)
        {
            // Only a cross-volume path (C:\ -> D:\) lands here. A stray event
            // from another drive is dropped, never indexed.
            _logger?.LogDebug(ex, "Dropped watcher event off the workspace volume: {Path}.", fullPath);
            return;
        }
        // Dropped when outside the root or under an excluded dir, so the
        // watcher batch can never index what a full pass would skip.
        if (CodebaseIndexService.IsSkippedPath(rel))
        {
            _logger?.LogDebug("Codebase watcher dropped {ChangeType} event for skipped path {Path}.", changeType, rel);
            return;
        }
        _logger?.LogDebug("Codebase watcher saw {ChangeType}: {Path}.", changeType, rel);
        _queue.Enqueue(rel);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            if (Interlocked.Exchange(ref _fullRescanRequested, 0) == 1)
            {
                while (_queue.TryDequeue(out _)) { }
                await RunFullIncrementalPassAsync("buffer overflow rescan", ct);
                return;
            }

            var batch = new HashSet<string>(StringComparer.Ordinal);
            while (_queue.TryDequeue(out var rel)) batch.Add(rel);
            if (batch.Count == 0) return;

            _logger?.LogDebug(
                "Codebase watcher drain: {Count} pending change(s): {Paths}.",
                batch.Count, string.Join(", ", batch));

            // Refresh ignore rules when a .gitignore itself changed, then
            // drop ignored paths: the batch must never index what a full
            // pass would skip.
            var gitignoreChanged = batch.Any(IsGitignoreName);
            if (_ignores is null || gitignoreChanged)
                _ignores = GitignoreFilter.Load(_svc.ProjectRoot, _logger);
            var beforeIgnoreCount = batch.Count;
            batch.RemoveWhere(rel => _ignores.IsIgnored(rel));
            if (beforeIgnoreCount != batch.Count)
            {
                _logger?.LogDebug(
                    "Codebase watcher .gitignore filtered out {Ignored} path(s).",
                    beforeIgnoreCount - batch.Count);
            }

            if (gitignoreChanged)
            {
                // Newly ignored rows live outside any scoped batch, so only
                // a full incremental pass can purge them. Hash-skipping keeps
                // it cheap: unchanged files are re-enumerated, not rewritten.
                await RunFullIncrementalPassAsync(".gitignore change", ct);
                return;
            }

            if (batch.Count == 0)
            {
                _logger?.LogInformation(
                    "Codebase watcher batch: all {Count} path(s) ignored by .gitignore.",
                    beforeIgnoreCount);
                return;
            }

            var swBatch = Stopwatch.StartNew();
            var batchResult = await _svc.IndexAsync(full: false, pathPrefixes: batch.ToList(), ct: ct);
            swBatch.Stop();
            _logger?.LogInformation(
                "Codebase watcher batch indexed files={Files} chunks={Chunks} paths={Paths} tookMs={TookMs}.",
                batchResult.Files, batchResult.Chunks, batch.Count, swBatch.ElapsedMilliseconds);
            await _notifier.NotifyAsync("codebase", ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Codebase watcher batch index failed.");
        }
    }

    private async Task RunFullIncrementalPassAsync(string reason, CancellationToken ct)
    {
        try
        {
            _logger?.LogInformation("Codebase watcher {Reason}: starting full incremental pass on {Root}...", reason, _svc.ProjectRoot);
            var sw = Stopwatch.StartNew();
            var result = await _svc.IndexAsync(full: false, ct: ct);
            sw.Stop();

            _logger?.LogInformation(
                "Codebase watcher {Reason}: completed files={Files} chunks={Chunks} skipped={Skipped} deleted={Deleted} tookMs={TookMs}.",
                reason, result.Files, result.Chunks, result.Skipped, result.Deleted, sw.ElapsedMilliseconds);

            await _notifier.NotifyAsync("codebase", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Codebase watcher {Reason} failed.", reason);
        }
    }
}
