using System.IO;
using Eling.Core.Docs;
using Eling.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend;

/// <summary>
/// Watches Markdown and metadata files for every active workspace. Only the
/// dashboard-owner process starts this watcher: it owns the SSE subscribers and
/// is long-lived, unlike ephemeral MCP stdio processes.
///
/// Each workspace has an O(1) dirty flag. The drain clears the flag BEFORE
/// reconciling, so a write landing mid-scan re-arms it and a burst of events
/// coalesces into one scan per tick. A failed scan re-arms the flag.
/// </summary>
public sealed class DocsDirWatcher : IDisposable
{
    public const string Topic = DocsChangeTopics.Docs;

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly object _sync = new();
    private readonly DocsService _docsService;
    private readonly MemoryChangeBroadcaster _memoryBroadcaster;
    private readonly TimeSpan _interval;
    private readonly ILogger<DocsDirWatcher> _logger;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWorkspacesRegistry? _workspacesRegistry;

    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;

    public DocsDirWatcher(
        DocsService docsService,
        MemoryChangeBroadcaster broadcaster,
        IWorkspacesRegistry? registry = null,
        ILogger<DocsDirWatcher>? logger = null)
    {
        _docsService = docsService ?? throw new ArgumentNullException(nameof(docsService));
        _memoryBroadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _workspacesRegistry = registry;
        _logger = logger ?? NullLogger<DocsDirWatcher>.Instance;
        _interval = DefaultInterval;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;
            ReconcileRoots();
            _ = Task.Run(() => CatchUpAsync());
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(() => DrainAsync(cts.Token));
            _started = true;
            _logger.LogInformation("Watching docs files across {Count} workspace(s)", _watchers.Count);
        }
    }

    private void ReconcileRoots()
    {
        if (_workspacesRegistry is not null)
        {
            var registered = _workspacesRegistry.ListAsync().GetAwaiter().GetResult()
                .Where(row => Directory.Exists(row.WorkspaceRoot))
                .Select(row => row.WorkspaceRoot)
                .ToList();
            _docsService.SetWorkspaceRoots(registered);
        }
        var roots = _docsService.WorkspaceRoots.Where(Directory.Exists).ToList();
        foreach (var removed in _watchers.Keys.Except(roots, StringComparer.OrdinalIgnoreCase).ToList())
        {
            _watchers[removed].Dispose();
            _watchers.Remove(removed);
            _dirty.Remove(removed);
            _pending.Remove(removed);
        }
        foreach (var root in roots)
        {
            if (_watchers.ContainsKey(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                watcher.Created += (_, args) => OnFileEvent(root, args.FullPath);
                watcher.Changed += (_, args) => OnFileEvent(root, args.FullPath);
                watcher.Deleted += (_, args) => OnFileEvent(root, args.FullPath);
                watcher.Renamed += (_, args) => OnFileEvent(root, args.OldFullPath, args.FullPath);
                watcher.Error += (_, args) => _logger.LogWarning(args.GetException(), "Docs watcher error in {Root}", root);
                watcher.EnableRaisingEvents = true;
                _watchers[root] = watcher;
                _dirty[root] = 1; // scan a newly registered workspace on the next tick
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not arm docs watcher for {Root}", root);
            }
        }
    }

    private void OnFileEvent(string root, string path, string? renamedTo = null)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(root, out var paths))
            {
                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _pending[root] = paths;
            }
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
            if (renamedTo is not null && renamedTo.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) paths.Add(renamedTo);
        }
    }

    private async Task CatchUpAsync()
    {
        foreach (var root in _watchers.Keys.ToList())
        {
            try { await _docsService.ScanAsync(workspaceRoot: root); }
            catch (Exception ex) { _logger.LogWarning(ex, "Startup docs scan failed for {Root}", root); }
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(_interval, cancellationToken); }
            catch (OperationCanceledException) { break; }

            lock (_sync) ReconcileRoots();
            foreach (var root in _watchers.Keys.ToList())
            {
                bool full;
                List<string> paths;
                lock (_sync)
                {
                    full = _dirty.GetValueOrDefault(root) != 0;
                    _dirty[root] = 0;
                    paths = _pending.TryGetValue(root, out var pending)
                        ? pending.ToList()
                        : [];
                    _pending.Remove(root);
                }
                if (!full && paths.Count == 0) continue;

                try
                {
                    var broadcast = full;
                    ScanResult? scan = null;
                    if (full)
                    {
                        scan = await _docsService.ScanAsync(workspaceRoot: root, ct: cancellationToken);
                    }
                    else
                    {
                        // Coverage is shared with the scanner: an event under
                        // node_modules/.git/... is not a docs change at all.
                        var coverage = await _docsService.GetCoveragePolicyAsync(root, cancellationToken);
                        var covered = paths.Where(p => DocsService.IsCoveredPath(p, root, coverage)).ToList();
                        foreach (var removed in covered.Where(p => !File.Exists(p)))
                        {
                            await _docsService.DeleteIndexedPathAsync(root, removed, cancellationToken);
                            broadcast = true;
                        }
                        var changed = covered.Where(File.Exists).ToList();
                        if (changed.Count > 0)
                        {
                            scan = await _docsService.ScanAsync(full: false, paths: changed, workspaceRoot: root, ct: cancellationToken);
                            broadcast = true;
                        }
                    }
                    // Another scan (startup catch-up, manual reindex) held the
                    // gate. Nothing was indexed, so keep the flag armed instead
                    // of dropping the event — the next tick retries.
                    if (scan?.AlreadyRunning == true)
                    {
                        ReArm(root, paths);
                        broadcast = false;
                    }
                    if (broadcast)
                    {
                        _memoryBroadcaster.Notify(
                            DocsChangeTopics.Docs,
                            DocsService.ResolveWorkspaceId(root),
                            root);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Docs rescan failed for {Root}; re-arming", root);
                    ReArm(root, paths);
                }
            }
        }
    }

    private void ReArm(string root, IEnumerable<string> paths)
    {
        lock (_sync)
        {
            _dirty[root] = 1;
            if (!_pending.TryGetValue(root, out var pending))
            {
                pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _pending[root] = pending;
            }
            foreach (var path in paths) pending.Add(path);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
            _dirty.Clear();
            _pending.Clear();
        }
    }
}
