using System.Collections.Concurrent;
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
            _watcher.Created += (_, e) => Enqueue(e.FullPath);
            _watcher.Changed += (_, e) => Enqueue(e.FullPath);
            _watcher.Deleted += (_, e) => Enqueue(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Enqueue(e.OldFullPath);
                Enqueue(e.FullPath);
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
        }
    }

    private static bool IsGitignoreName(string rel) =>
        Path.GetFileName(rel.Replace('\\', '/')).Equals(".gitignore", StringComparison.Ordinal);

    private void Enqueue(string fullPath)
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
        if (CodebaseIndexService.IsSkippedPath(rel)) return;
        _queue.Enqueue(rel);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            if (Interlocked.Exchange(ref _fullRescanRequested, 0) == 1)
            {
                while (_queue.TryDequeue(out _)) { }
                var rescan = await _svc.IndexAsync(ct: ct);
                _logger?.LogInformation(
                    "Codebase watcher full incremental rescan completed files={Files} chunks={Chunks}.",
                    rescan.Files, rescan.Chunks);
                await _notifier.NotifyAsync("codebase", ct);
                return;
            }

            var batch = new HashSet<string>(StringComparer.Ordinal);
            while (_queue.TryDequeue(out var rel)) batch.Add(rel);
            if (batch.Count == 0) return;

            // Refresh ignore rules when a .gitignore itself changed, then
            // drop ignored paths: the batch must never index what a full
            // pass would skip.
            var gitignoreChanged = batch.Any(IsGitignoreName);
            if (_ignores is null || gitignoreChanged)
                _ignores = GitignoreFilter.Load(_svc.ProjectRoot, _logger);
            batch.RemoveWhere(rel => _ignores.IsIgnored(rel));

            if (gitignoreChanged)
            {
                // Newly ignored rows live outside any scoped batch, so only
                // a full incremental pass can purge them. Hash-skipping keeps
                // it cheap: unchanged files are re-enumerated, not rewritten.
                var fullResult = await _svc.IndexAsync(ct: ct);
                _logger?.LogInformation(
                    "Codebase watcher .gitignore change: full incremental pass files={Files} chunks={Chunks}.",
                    fullResult.Files, fullResult.Chunks);
                await _notifier.NotifyAsync("codebase", ct);
                return;
            }

            if (batch.Count == 0) return;

            var batchResult = await _svc.IndexAsync(full: false, pathPrefixes: batch.ToList(), ct: ct);
            _logger?.LogDebug(
                "Codebase watcher batch indexed files={Files} chunks={Chunks} paths={Paths}.",
                batchResult.Files, batchResult.Chunks, batch.Count);
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
}
