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
    private readonly Dictionary<string, int> _metadataDirty = new(StringComparer.OrdinalIgnoreCase);
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
                watcher.Renamed += (_, args) => OnFileEvent(root, args.FullPath);
                watcher.Error += (_, args) => _logger.LogWarning(args.GetException(), "Docs watcher error in {Root}", root);
                watcher.EnableRaisingEvents = true;
                _watchers[root] = watcher;
                _dirty[root] = 1; // scan a newly registered workspace on the next tick
                _metadataDirty[root] = 1;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not arm docs watcher for {Root}", root);
            }
        }
    }

    private void OnFileEvent(string root, string path)
    {
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("docs-metadata.json", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("docs-local-metadata.json", StringComparison.OrdinalIgnoreCase))
            return;
        lock (_sync)
        {
            if (_dirty.ContainsKey(root)) _dirty[root] = 1;
            if (path.EndsWith("docs-metadata.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("docs-local-metadata.json", StringComparison.OrdinalIgnoreCase))
                _metadataDirty[root] = 1;
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
                int wasDirty;
                lock (_sync) wasDirty = _dirty.GetValueOrDefault(root);
                if (wasDirty == 0) continue;
                int metadataDirty;
                lock (_sync)
                {
                    _dirty[root] = 0;
                    metadataDirty = _metadataDirty.GetValueOrDefault(root);
                    _metadataDirty[root] = 0;
                }
                var full = metadataDirty == 0;
                try
                {
                    await _docsService.ScanAsync(full: full, workspaceRoot: root, ct: cancellationToken);
                    _memoryBroadcaster.Notify(DocsChangeTopics.Docs);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Docs rescan failed for {Root}; re-arming", root);
                    lock (_sync) _dirty[root] = 1;
                }
            }
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
        }
    }
}
