using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend;

/// <summary>
/// Watches the shared codebase index directory — where every eling process
/// writes its per-workspace <c>*.db</c> FTS file — and turns index changes
/// into SSE <c>"codebase"</c> broadcasts.
///
/// Rationale: each backend indexes only its own workspace, but all of them
/// share the global store directory. A dashboard owner therefore never hears
/// about a peer that rebuilds its index on another instance (agent-driven
/// <c>codebase_index</c> calls included). Watching the directory restores
/// near-real-time refresh for every connected SSE subscriber.
///
/// Only <c>Changed</c>/<c>Created</c> on <c>*.db</c> files are monitored;
/// the <c>-wal</c>/<c>-shm</c> sidecars are ignored. Events are debounced so
/// bursts (one rebuild touches several files) coalesce into one broadcast.
/// </summary>
public sealed class CodebaseDirWatcher : IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    private readonly object _sync = new();
    private readonly MemoryChangeBroadcaster _broadcaster;
    private readonly ILogger<CodebaseDirWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private bool _started;
    private bool _disposed;

    public CodebaseDirWatcher(
        string codebaseDirectory,
        MemoryChangeBroadcaster broadcaster,
        ILogger<CodebaseDirWatcher>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(codebaseDirectory))
        {
            throw new ArgumentException("A codebase directory is required.", nameof(codebaseDirectory));
        }
        Directory.CreateDirectory(codebaseDirectory);

        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _logger = logger ?? NullLogger<CodebaseDirWatcher>.Instance;
        _watcher = new FileSystemWatcher(codebaseDirectory)
        {
            Filter = "*.db",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = false
        };
    }

    /// <summary>
    /// Starts raising index-change events. Idempotent; a disposed watcher
    /// cannot be restarted.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started || _watcher is null) return;

            _watcher.Created += OnFileEvent;
            _watcher.Changed += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
            _started = true;

            _logger.LogInformation(
                "Watching shared codebase directory for index changes: {Path}",
                _watcher.Path);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Debounce: later events supersede earlier ones within the window.
        CancellationToken token;
        lock (_sync)
        {
            if (_disposed) return;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = DebounceAndNotifyAsync(token);
    }

    private async Task DebounceAndNotifyAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceWindow, token);
            lock (_sync)
            {
                if (_disposed) return;
            }

            _broadcaster.Notify("codebase");
            _logger.LogDebug("Broadcast codebase change (debounced)");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change within the debounce window; the
            // latest event performs the broadcast.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error broadcasting codebase change");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Internal buffer overflow can drop events; broadcast as a safe
        // fallback so subscribers re-sync from the API.
        _logger.LogWarning(
            e.GetException(),
            "Codebase directory watcher error; broadcasting codebase as a fallback");
        _broadcaster.Notify("codebase");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileEvent;
                _watcher.Changed -= OnFileEvent;
                _watcher.Renamed -= OnFileEvent;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
                _watcher = null;
            }

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }
}
