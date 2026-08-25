using Eling.Application;
using Eling.Core;

namespace Eling.Dashboard;

/// <summary>
/// In-memory registry of active eling runtimes plus the liveness sweeper.
/// Lifecycle rule: once at least one runtime has registered, an empty registry
/// (after a short bounded debounce) shuts the dashboard down. The dashboard is
/// never permanently owned by the first process that started it.
/// </summary>
public sealed class RuntimeRegistry : IDisposable
{
    private readonly object _lock = new();
    private readonly List<RuntimeInfo> _runtimes = [];
    private readonly Dictionary<string, IMemoryService> _memoryByDataDirectory = [];
    private readonly ILogger<RuntimeRegistry> _logger;
    private readonly Timer _sweeper;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _removeGrace;
    private readonly TimeSpan _emptyShutdownDebounce;
    private Timer? _shutdownTimer;
    private bool _everRegistered;
    private bool disposedValue;

    public Action? ShutdownCallback { get; set; }

    public RuntimeRegistry(ILogger<RuntimeRegistry> logger)
    {
        _logger = logger;
        // Intervals are env-tunable so lifecycle tests don't wait real minutes.
        _staleAfter = FromEnvironment("ELING_TEST_STALE_MS", TimeSpan.FromSeconds(30));
        _removeGrace = FromEnvironment("ELING_TEST_GRACE_MS", TimeSpan.FromSeconds(60));
        _emptyShutdownDebounce = FromEnvironment("ELING_TEST_SHUTDOWN_DEBOUNCE_MS", TimeSpan.FromSeconds(5));
        var sweepPeriod = FromEnvironment("ELING_TEST_SWEEP_MS", TimeSpan.FromSeconds(10));
        _sweeper = new Timer(_ => Sweep(), null, sweepPeriod, sweepPeriod);
    }

    public void Register(RuntimeRegistration registration)
    {
        lock (_lock)
        {
            _everRegistered = true;
            ResetShutdownTimer();

            var existing = _runtimes.FirstOrDefault(r => r.ProcessId == registration.ProcessId);
            if (existing is not null)
            {
                existing.ProjectRoot = registration.ProjectRoot;
                existing.DataDirectory = registration.DataDirectory;
                existing.StartTime = registration.StartTime;
                existing.McpEnabled = registration.McpEnabled;
                existing.McpTransport = registration.McpTransport;
                existing.LastHeartbeat = DateTimeOffset.UtcNow;
                existing.IsAlive = true;
                return;
            }

            _runtimes.Add(new RuntimeInfo
            {
                ProcessId = registration.ProcessId,
                ProjectRoot = registration.ProjectRoot,
                DataDirectory = registration.DataDirectory,
                StartTime = registration.StartTime,
                McpEnabled = registration.McpEnabled,
                McpTransport = registration.McpTransport,
                LastHeartbeat = DateTimeOffset.UtcNow,
                IsAlive = true
            });

            _logger.LogInformation(
                "Runtime registered: pid={Pid} root={Root}",
                registration.ProcessId, registration.ProjectRoot);
        }
    }

    public bool Heartbeat(int processId)
    {
        lock (_lock)
        {
            var runtime = _runtimes.FirstOrDefault(r => r.ProcessId == processId);
            if (runtime is null) return false;

            runtime.LastHeartbeat = DateTimeOffset.UtcNow;
            runtime.IsAlive = true;
            return true;
        }
    }

    public bool Unregister(int processId)
    {
        lock (_lock)
        {
            var removed = _runtimes.RemoveAll(r => r.ProcessId == processId) > 0;
            if (removed)
            {
                _logger.LogInformation("Runtime unregistered: pid={Pid}", processId);
            }

            return removed;
        }
    }

    public IReadOnlyList<RuntimeInfo> Alive()
    {
        lock (_lock)
        {
            return _runtimes.Where(r => r.IsAlive).ToList();
        }
    }

    /// <summary>
    /// Memory API for the most recently started alive runtime. The dashboard
    /// never owns a project .eling itself; it borrows the data directory of a
    /// registered runtime. Registry stays runtime-coordination only.
    /// </summary>
    public IMemoryService ResolveMemoryService()
    {
        lock (_lock)
        {
            var latest = _runtimes
                .Where(r => r.IsAlive)
                .OrderByDescending(r => r.StartTime)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No active project runtime is registered.");

            var directory = latest.DataDirectory;
            if (!_memoryByDataDirectory.TryGetValue(directory, out var service))
            {
                service = new MemoryService(
                    new FileSystemMemoryStorage(directory),
                    new SqliteMemoryIndex(Path.Combine(directory, "index.db")));
                _memoryByDataDirectory[directory] = service;
            }

            return service;
        }
    }

    private void Sweep()
    {
        bool anyAlive;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var runtime in _runtimes.Where(r => r.IsAlive && now - r.LastHeartbeat > _staleAfter))
            {
                runtime.IsAlive = false;
                _logger.LogWarning("Runtime stale: pid={Pid} root={Root}", runtime.ProcessId, runtime.ProjectRoot);
            }

            _runtimes.RemoveAll(r => !r.IsAlive && now - r.LastHeartbeat > _removeGrace);

            // Drop memory services whose owning runtime is gone so the cache
            // mirrors live projects only (services are stateless: SQLite
            // connections open per operation).
            var liveDirectories = _runtimes.Where(r => r.IsAlive).Select(r => r.DataDirectory).ToHashSet();
            foreach (var directory in _memoryByDataDirectory.Keys.Where(d => !liveDirectories.Contains(d)).ToList())
            {
                _memoryByDataDirectory.Remove(directory);
            }

            anyAlive = _runtimes.Any(r => r.IsAlive);

            if (_everRegistered && !anyAlive && _shutdownTimer is null)
            {
                ScheduleEmptyShutdown();
            }
        }
    }

    private void ScheduleEmptyShutdown()
    {
        _shutdownTimer = new Timer(_ =>
        {
            bool stillEmpty;
            lock (_lock)
            {
                stillEmpty = !_runtimes.Any(r => r.IsAlive);
                if (!stillEmpty)
                {
                    // A runtime came back during the debounce window; re-arm nothing,
                    // the next sweep will schedule again if needed.
                    _shutdownTimer?.Dispose();
                    _shutdownTimer = null;
                }
            }

            if (stillEmpty)
            {
                _logger.LogInformation("No active runtimes remain; shutting dashboard down.");
                ShutdownCallback?.Invoke();
            }
        }, null, _emptyShutdownDebounce, Timeout.InfiniteTimeSpan);
    }

    private void ResetShutdownTimer()
    {
        _shutdownTimer?.Dispose();
        _shutdownTimer = null;
    }

    private static TimeSpan FromEnvironment(string name, TimeSpan fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : fallback;

    private void Dispose(bool disposing)
    {
        if (disposedValue) return;
        if (disposing)
        {
            _sweeper.Dispose();
            ResetShutdownTimer();
        }

        disposedValue = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
