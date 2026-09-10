using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend;

/// <summary>
/// Watches the shared runtime directory — where every eling process writes its
/// <c>{pid}.json</c> registration file — and turns membership changes into SSE
/// <c>"runtimes"</c> broadcasts.
///
/// Rationale: runtimes self-register in their own in-process registry and
/// coordinate across processes through the shared directory files, not through
/// events. A dashboard owner therefore never hears about a peer runtime that
/// registers or exits on another instance. Watching the directory restores the
/// near-real-time behaviour the old HTTP <c>/api/coordinator/register</c>
/// broadcast provided, for every connected SSE subscriber (desktop, web UI).
///
/// Only <c>Created</c>/<c>Deleted</c>/<c>Renamed</c> are monitored. Heartbeat
/// refreshes only touch file timestamps (a <c>Changed</c> event), so they are
/// deliberately ignored to avoid notifying on every 15 s heartbeat. Events are
/// debounced so bursts (N runtimes starting together) coalesce into one
/// broadcast.
/// </summary>
public sealed class RuntimeDirWatcher : IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly object _sync = new();
    private readonly MemoryChangeBroadcaster _broadcaster;
    private readonly ILogger<RuntimeDirWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private bool _started;
    private bool _disposed;

    public RuntimeDirWatcher(
        string runtimeDirectory,
        MemoryChangeBroadcaster broadcaster,
        ILogger<RuntimeDirWatcher>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            throw new ArgumentException("A runtime directory is required.", nameof(runtimeDirectory));
        }
        Directory.CreateDirectory(runtimeDirectory);

        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _logger = logger ?? NullLogger<RuntimeDirWatcher>.Instance;
        _watcher = new FileSystemWatcher(runtimeDirectory)
        {
            Filter = "*.json",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            IncludeSubdirectories = false
        };
    }

    /// <summary>
    /// Starts raising membership events. Idempotent; a disposed watcher cannot
    /// be restarted.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started || _watcher is null) return;

            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
            _started = true;

            _logger.LogInformation(
                "Watching shared runtime directory for membership changes: {Path}",
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

            _broadcaster.Notify("runtimes");
            _logger.LogDebug("Broadcast runtimes change (debounced)");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change within the debounce window; the
            // latest event performs the broadcast.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error broadcasting runtimes change");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Internal buffer overflow can drop events; broadcast as a safe
        // fallback so subscribers re-sync from the API.
        _logger.LogWarning(
            e.GetException(),
            "Runtime directory watcher error; broadcasting runtimes as a fallback");
        _broadcaster.Notify("runtimes");
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
                _watcher.Deleted -= OnFileEvent;
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