using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend;

/// <summary>
/// Watches the shared database registry — the single <c>databases.json</c> every
/// eling process reads and writes — and turns registry changes into SSE
/// <c>"databases"</c> broadcasts.
///
/// Rationale: the registry is global, but the dashboard's connection list is
/// fetched on mount and on demand. A connection added by the agent's
/// <c>db_import_connections</c> call, by a second browser tab, or by another
/// eling process therefore stayed invisible until somebody pressed Refresh.
/// Watching the file restores the same near-real-time behaviour the codebase
/// index already has (see <see cref="CodebaseDirWatcher"/>): the dashboard
/// owner notices the change no matter which process wrote it.
///
/// Only the registry file itself is monitored — the <c>.tmp</c> file the store
/// writes on its way to an atomic replace is ignored. Events are debounced so
/// a burst of writes coalesces into one broadcast.
/// </summary>
public sealed class DatabaseRegistryWatcher : IDisposable
{
    /// <summary>The topic this watcher broadcasts on.</summary>
    public const string Topic = "databases";

    /// <summary>The registry file name inside the shared agent directory.</summary>
    public const string RegistryFileName = "databases.json";

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    private readonly object _sync = new();
    private readonly MemoryChangeBroadcaster _broadcaster;
    private readonly ILogger<DatabaseRegistryWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private bool _started;
    private bool _disposed;

    public DatabaseRegistryWatcher(
        string agentDirectory,
        MemoryChangeBroadcaster broadcaster,
        ILogger<DatabaseRegistryWatcher>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(agentDirectory))
        {
            throw new ArgumentException("An agent directory is required.", nameof(agentDirectory));
        }
        Directory.CreateDirectory(agentDirectory);

        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _logger = logger ?? NullLogger<DatabaseRegistryWatcher>.Instance;
        _watcher = new FileSystemWatcher(agentDirectory)
        {
            Filter = RegistryFileName,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = false
        };
    }

    /// <summary>
    /// Starts raising registry-change events. Idempotent; a disposed watcher
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
                "Watching the shared database registry for changes: {Path}",
                Path.Combine(_watcher.Path, RegistryFileName));
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

            _broadcaster.Notify(Topic);
            _logger.LogDebug("Broadcast database registry change (debounced)");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change within the debounce window; the
            // latest event performs the broadcast.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error broadcasting database registry change");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Internal buffer overflow can drop events; broadcast as a safe
        // fallback so subscribers re-sync from the API.
        _logger.LogWarning(
            e.GetException(),
            "Database registry watcher error; broadcasting {Topic} as a fallback",
            Topic);
        _broadcaster.Notify(Topic);
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
