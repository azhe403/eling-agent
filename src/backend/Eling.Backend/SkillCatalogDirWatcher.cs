using System.IO;
using Eling.Core.Scope;
using Eling.Core.Skill;
using Eling.Core.Skill.Scanning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend;

/// <summary>
/// Watches the machine-global skill catalog (<c>~/.agents/skills</c>) and turns
/// on-disk changes — writes from any process, not just eling-mediated ones —
/// into SSE <c>"skills"</c> broadcasts after a full catalog rescan.
///
/// Rationale: the catalog is a shared folder; an external tool can drop a
/// skill into it at any time, and no eling API call runs in that process, so
/// the mutation-side notifier never fires. The dashboard owner is the only
/// process with SSE subscribers, so this watcher rides it the same way the
/// audit watcher rides the store files: a dirty flag per event (O(1), no
/// I/O, exceptions impossible) and a fixed-interval drain that clears the
/// flag BEFORE reconciling, so a write landing mid-scan re-sets the flag and
/// the next tick rescans — nothing is lost, and a burst of writes coalesces
/// into at most one scan per tick.
///
/// A rescan is the reconcile: the REST list reads the SQLite index, not the
/// disk, so a new external skill only becomes visible after
/// <see cref="ISkillCatalogScanner.ScanAsync"/> runs; the broadcast fires
/// after the scan on purpose. A scan failure is logged and swallowed; the
/// next file event flags the catalog dirty again and the drain retries.
/// </summary>
public sealed class SkillCatalogDirWatcher : IDisposable
{
    /// <summary>The topic this watcher broadcasts on.</summary>
    public const string Topic = SkillChangeTopics.Skills;

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly object _sync = new();
    private readonly ISkillCatalogScanner _scanner;
    private readonly MemoryChangeBroadcaster _broadcaster;
    private readonly string _skillsDirectory;
    private readonly TimeSpan _interval;
    private readonly ILogger<SkillCatalogDirWatcher> _logger;

    // All accesses go through Interlocked so the drain's clear-before-work is
    // atomic with respect to the event handlers' set. (Interlocked is used
    // instead of volatile+plain so the drain can atomically exchange.)
    private int _dirty;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;

    public SkillCatalogDirWatcher(
        ISkillCatalogScanner scanner,
        MemoryChangeBroadcaster broadcaster,
        string? skillsDirectory = null,
        TimeSpan? interval = null,
        ILogger<SkillCatalogDirWatcher>? logger = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _skillsDirectory = skillsDirectory ?? SkillCatalogPaths.SkillsDirectory;
        _interval = interval ?? DefaultInterval;
        _logger = logger ?? NullLogger<SkillCatalogDirWatcher>.Instance;
    }

    /// <summary>
    /// Starts watching the catalog and draining the dirty flag. Idempotent; a
    /// disposed watcher cannot be restarted. Started by the dashboard owner
    /// only — peers own no SSE subscribers, so watching there would burn
    /// handles for nobody.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;

            try
            {
                Directory.CreateDirectory(_skillsDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not create the skill catalog directory {Directory}; the catalog watcher is not running.",
                    _skillsDirectory);
                return;
            }

            // No Filter is set: the default matches everything (the catalog
            // holds only skill folders, so there is nothing to exclude).
            _watcher = new FileSystemWatcher(_skillsDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
            };
            _watcher.Created += OnFileEvent;
            _watcher.Changed += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(() => DrainAsync(cts));
            _started = true;

            _logger.LogInformation(
                "Watching the skill catalog for changes: {Directory}",
                _skillsDirectory);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        Interlocked.Exchange(ref _dirty, 1);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // A buffer overflow can drop events; the rescan is the reconcile, so
        // the only thing to do is flag the catalog dirty and let the drain
        // rescan in full.
        _logger.LogWarning(
            e.GetException(),
            "Skill catalog watcher error; flagging the catalog dirty for a full rescan.");
        Interlocked.Exchange(ref _dirty, 1);
    }

    private async Task DrainAsync(CancellationTokenSource cts)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                // Clear BEFORE work: a write landing mid-scan re-sets the
                // flag and the next tick rescans — nothing is lost.
                if (Interlocked.Exchange(ref _dirty, 0) == 0) continue;

                try
                {
                    await _scanner.ScanAsync().ConfigureAwait(false);
                    await _broadcaster.NotifyAsync(Topic).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skill catalog rescan failed; the next event will retry.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose cancelled the loop; the watcher stops with it.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill catalog watcher loop stopped unexpectedly.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_watcher is not null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnFileEvent;
                    _watcher.Changed -= OnFileEvent;
                    _watcher.Deleted -= OnFileEvent;
                    _watcher.Renamed -= OnFileEvent;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose the skill catalog watcher cleanly.");
            }

            try
            {
                _cts?.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cancel the skill catalog drain loop.");
            }

            try
            {
                _cts?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose the skill catalog drain token source.");
            }

            _cts = null;
        }
    }
}
