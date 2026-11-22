using System.IO;
using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend;

/// <summary>
/// Watches the docs directories (docs/ and .eling/docs/) and turns
/// on-disk changes — writes from any process, not just eling-mediated ones —
/// into SSE "docs" broadcasts after a full catalog rescan.
///
/// Rationale: the docs directories are shared folders; an external tool can
/// drop a document into them at any time, and no eling API call runs in that
/// process, so the mutation-side notifier never fires. The dashboard owner is
/// the only process with SSE subscribers, so this watcher rides it the same
/// way the audit watcher rides the store files: a dirty flag per event (O(1),
/// no I/O, exceptions impossible) and a fixed-interval drain that clears the
/// flag BEFORE reconciling, so a write landing mid-scan re-sets the flag and
/// the next tick rescans — nothing is lost, and a burst of writes coalesces
/// into at most one scan per tick.
///
/// A rescan is the reconcile: the REST list reads the SQLite index, not the
/// disk, so a new external document only becomes visible after
/// <see cref="DocsService.ScanAsync"/> runs; the broadcast fires after the
/// scan on purpose. A scan failure is logged and the dirty flag is re-armed,
/// so the drain retries on the next tick even without a file event.
/// </summary>
public sealed class DocsDirWatcher : IDisposable
{
    /// <summary>
    /// The topic this watcher broadcasts on.
    /// </summary>
    public const string Topic = DocsChangeTopics.Docs;

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly object _sync = new();
    private readonly DocsService _docsService;
    private readonly MemoryChangeBroadcaster _memoryBroadcaster;
    private readonly string _workspaceRoot;
    private readonly TimeSpan _interval;
    private readonly ILogger<DocsDirWatcher> _logger;

    // All accesses go through Interlocked so the drain's clear-before-work is
    // atomic with respect to the event handlers' set. (Interlocked is used
    // instead of volatile+plain so the drain can atomically exchange.)
    private int _dirty;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;

    public DocsDirWatcher(
        DocsService docsService,
        MemoryChangeBroadcaster broadcaster,
        string workspaceRoot,
        TimeSpan? interval = null,
        ILogger<DocsDirWatcher>? logger = null)
    {
        _docsService = docsService ?? throw new ArgumentNullException(nameof(docsService));
        _memoryBroadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));
        }
        if (interval is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The drain interval must be positive.");
        }
        _workspaceRoot = workspaceRoot;
        _interval = interval ?? DefaultInterval;
        _logger = logger ?? NullLogger<DocsDirWatcher>.Instance;
    }

    /// <summary>
    /// Starts watching the docs directories and draining the dirty flag.
    /// Idempotent; a disposed watcher cannot be restarted.
    /// Started by the dashboard owner only — peers own no SSE subscribers,
    /// so watching there would burn handles for nobody.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;

            var directoriesToWatch = new[]
            {
                Path.Combine(_workspaceRoot, "docs"),
                Path.Combine(_workspaceRoot, ".eling", "docs")
            };

            try
            {
                foreach (var dir in directoriesToWatch)
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not create one or more docs directories; the docs watcher is not running.");
                return;
            }

            try
            {
                // Watch the docs/ directory
                var docsWatcher = new FileSystemWatcher(Path.Combine(_workspaceRoot, "docs"))
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite
                        | NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                };
                docsWatcher.Created += OnFileEvent;
                docsWatcher.Changed += OnFileEvent;
                docsWatcher.Deleted += OnFileEvent;
                docsWatcher.Renamed += OnFileEvent;
                docsWatcher.Error += OnWatcherError;
                docsWatcher.EnableRaisingEvents = true;

                // Watch the .eling/docs/ directory
                var elingDocsWatcher = new FileSystemWatcher(Path.Combine(_workspaceRoot, ".eling", "docs"))
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite
                        | NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                };
                elingDocsWatcher.Created += OnFileEvent;
                elingDocsWatcher.Changed += OnFileEvent;
                elingDocsWatcher.Deleted += OnFileEvent;
                elingDocsWatcher.Renamed += OnFileEvent;
                elingDocsWatcher.Error += OnWatcherError;
                elingDocsWatcher.EnableRaisingEvents = true;

                _watcher = docsWatcher; // Keep reference for disposal
                // Note: we need to track both watchers for disposal
                // For simplicity, we'll store them in a list, but since we only have two,
                // we can track the second one separately or use a collection.
                // Let's use a collection approach by creating a wrapper.
                _elingDocsWatcher = elingDocsWatcher;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not arm the docs watchers; the docs watcher is not running.");
                _watcher?.Dispose();
                _elingDocsWatcher?.Dispose();
                _watcher = null;
                _elingDocsWatcher = null;
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(() => DrainAsync(cts));
            _started = true;

            _logger.LogInformation(
                "Watching docs directories for changes: {WorkspaceRoot}",
                _workspaceRoot);
        }
    }

    private FileSystemWatcher? _elingDocsWatcher;

    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        Interlocked.Exchange(ref _dirty, 1);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "Docs watcher error; re-arming dirty flag.");
        Interlocked.Exchange(ref _dirty, 1);
    }

    private async Task DrainAsync(CancellationTokenSource cts)
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Clear the dirty flag BEFORE doing the work so a write landing
            // mid-scan re-sets the flag and the next tick rescans.
            var wasDirty = Interlocked.Exchange(ref _dirty, 0);
            if (wasDirty == 0)
            {
                continue;
            }

            try
            {
                await _docsService.ScanAsync(full: false, paths: null, ct: cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Docs rescan failed; re-arming dirty flag for retry.");
                // Re-arm the dirty flag so the drain retries on the next tick
                // even without a file event.
                Interlocked.Exchange(ref _dirty, 1);
                continue;
            }

            // Broadcast after the scan on purpose — the index is the source of
            // truth for the dashboard, so the event should fire after it is fresh.
            _memoryBroadcaster.Notify(Topic);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;

            _cts?.Cancel();
            _watcher?.Dispose();
            _elingDocsWatcher?.Dispose();
            _watcher = null;
            _elingDocsWatcher = null;
            _cts?.Dispose();
            _cts = null;
            _disposed = true;
        }
    }
}