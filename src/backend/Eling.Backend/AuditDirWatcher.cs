using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend;

/// <summary>
/// Watches the machine-global audit store — where every eling process appends
/// its change and access JSONL — and turns file changes into per-trail SSE
/// broadcasts (<c>"audit:changes"</c> / <c>"audit:access"</c>).
///
/// Rationale: audit writers live in every process (the dashboard owner plus
/// MCP-only peers), and a peer's append has no subscriber to notify — the
/// memory-notification relay only covers memory mutations. The store files
/// themselves are the one place every writer touches, so watching them
/// restores a precise live signal: a broadcast means that trail grew, from
/// whichever process wrote it. Dashboard pages subscribe to exactly the
/// trail they display instead of guessing from operation broadcasts.
///
/// Only appends and day-file arrivals matter, so — unlike the runtime watcher,
/// which ignores timestamp touches — this watcher listens to
/// <c>Changed</c> (JSONL appends change size and last-write) as well as
/// <c>Created</c>/<c>Deleted</c>/<c>Renamed</c> (rotation compressing a day,
/// retention pruning one). Each trail is watched and debounced independently,
/// so a burst of access writes never forces a changes-view refetch. The
/// <c>rollups/</c> directory is deliberately unwatched: maintenance writes
/// aggregates no live view displays.
/// </summary>
public sealed class AuditDirWatcher : IDisposable
{
    public const string ChangesTopic = "audit:changes";

    public const string AccessTopic = "audit:access";

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly object _sync = new();
    private readonly MemoryChangeBroadcaster _broadcaster;
    private readonly ILogger<AuditDirWatcher> _logger;
    private readonly FileSystemWatcher _changes;
    private readonly FileSystemWatcher _access;
    private CancellationTokenSource? _changesDebounce;
    private CancellationTokenSource? _accessDebounce;
    private bool _started;
    private bool _disposed;

    public AuditDirWatcher(
        string auditRoot,
        MemoryChangeBroadcaster broadcaster,
        ILogger<AuditDirWatcher>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(auditRoot))
        {
            throw new ArgumentException("An audit root directory is required.", nameof(auditRoot));
        }

        var changesDir = Path.Combine(auditRoot, "changes");
        var accessDir = Path.Combine(auditRoot, "access");
        Directory.CreateDirectory(changesDir);
        Directory.CreateDirectory(accessDir);

        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _logger = logger ?? NullLogger<AuditDirWatcher>.Instance;
        _changes = TrailWatcher(changesDir);
        _access = TrailWatcher(accessDir);
    }

    private static FileSystemWatcher TrailWatcher(string directory) =>
        new(directory)
        {
            Filter = "*.jsonl*",
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
            IncludeSubdirectories = false
        };

    /// <summary>
    /// Starts raising per-trail broadcasts. Idempotent; a disposed watcher
    /// cannot be restarted. Started by the dashboard owner only — peers own
    /// no SSE subscribers, so watching there would burn handles for nobody.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;

            Attach(_changes, ChangesTopic, v => _changesDebounce = v, () => _changesDebounce);
            Attach(_access, AccessTopic, v => _accessDebounce = v, () => _accessDebounce);
            _started = true;

            _logger.LogInformation(
                "Watching audit store for trail changes: {Changes} and {Access}",
                _changes.Path,
                _access.Path);
        }
    }

    private void Attach(
        FileSystemWatcher watcher,
        string topic,
        Action<CancellationTokenSource?> setDebounce,
        Func<CancellationTokenSource?> getDebounce)
    {
        void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            // Debounce: later events supersede earlier ones within the window.
            CancellationToken token;
            lock (_sync)
            {
                if (_disposed) return;
                getDebounce()?.Cancel();
                getDebounce()?.Dispose();
                var cts = new CancellationTokenSource();
                setDebounce(cts);
                token = cts.Token;
            }

            _ = DebounceAndNotifyAsync(topic, token);
        }

        watcher.Created += OnFileEvent;
        watcher.Changed += OnFileEvent;
        watcher.Deleted += OnFileEvent;
        watcher.Renamed += OnFileEvent;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
    }

    private async Task DebounceAndNotifyAsync(string topic, CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceWindow, token);
            lock (_sync)
            {
                if (_disposed) return;
            }

            _broadcaster.Notify(topic);
            _logger.LogDebug("Broadcast audit trail change ({Topic}, debounced)", topic);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change within the debounce window; the
            // latest event performs the broadcast.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error broadcasting audit trail change ({Topic})", topic);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Internal buffer overflow can drop events; broadcast both trails as a
        // safe fallback so subscribers re-sync from the API.
        _logger.LogWarning(
            e.GetException(),
            "Audit directory watcher error; broadcasting both trails as a fallback");
        _broadcaster.Notify(ChangesTopic);
        _broadcaster.Notify(AccessTopic);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _changesDebounce?.Cancel();
            _changesDebounce?.Dispose();
            _accessDebounce?.Cancel();
            _accessDebounce?.Dispose();
        }

        _changes.Dispose();
        _access.Dispose();
    }
}
