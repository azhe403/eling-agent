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
    private readonly UserScope _userScope;
    private IMemoryService? _globalService;
    private readonly IMemoryMerger _merger = new MemoryMerger();

    public Action? ShutdownCallback { get; set; }

    private readonly string _runtimeDir;

    public RuntimeRegistry(ILogger<RuntimeRegistry> logger, UserScope? userScope = null)
    {
        _logger = logger;
        _userScope = userScope ?? UserScope.Resolve(Environment.GetEnvironmentVariable("ELING_USER_SCOPE"));
        _runtimeDir = _userScope.RuntimeDirectory;
        Directory.CreateDirectory(_runtimeDir);

        // Intervals are env-tunable so lifecycle tests don't wait real minutes.
        _staleAfter = FromEnvironment("ELING_TEST_STALE_MS", TimeSpan.FromSeconds(30));
        _removeGrace = FromEnvironment("ELING_TEST_GRACE_MS", TimeSpan.FromSeconds(60));
        _emptyShutdownDebounce = FromEnvironment("ELING_TEST_SHUTDOWN_DEBOUNCE_MS", TimeSpan.FromSeconds(5));
        var sweepPeriod = FromEnvironment("ELING_TEST_SWEEP_MS", TimeSpan.FromSeconds(10));
        _sweeper = new Timer(_ => Sweep(), null, sweepPeriod, sweepPeriod);

        // Load existing runtime files on start to sync across dashboard processes
        SyncFromDisk();
    }

    private void SyncFromDisk()
    {
        lock (_lock)
        {
            if (!Directory.Exists(_runtimeDir)) return;
            var now = DateTimeOffset.UtcNow;
            
            // Read active files from disk
            var files = Directory.GetFiles(_runtimeDir, "*.json");
            var diskPids = new HashSet<int>();
            
            foreach (var file in files)
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (!int.TryParse(fileName, out var pid)) continue;

                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (now - lastWrite > _staleAfter)
                    {
                        File.Delete(file);
                        continue;
                    }

                    diskPids.Add(pid);
                    var content = File.ReadAllText(file);
                    var reg = System.Text.Json.JsonSerializer.Deserialize(content, CoordinatorJsonContext.Default.RuntimeRegistration);
                    if (reg is null) continue;

                    _everRegistered = true;
                    var existing = _runtimes.FirstOrDefault(r => r.ProcessId == reg.ProcessId);
                    if (existing is not null)
                    {
                        existing.ProjectRoot = reg.ProjectRoot;
                        existing.DataDirectory = reg.DataDirectory;
                        existing.StartTime = reg.StartTime;
                        existing.McpEnabled = reg.McpEnabled;
                        existing.McpTransport = reg.McpTransport;
                        existing.LastHeartbeat = lastWrite;
                        existing.IsAlive = true;
                    }
                    else
                    {
                        _runtimes.Add(new RuntimeInfo
                        {
                            ProcessId = reg.ProcessId,
                            ProjectRoot = reg.ProjectRoot,
                            DataDirectory = reg.DataDirectory,
                            StartTime = reg.StartTime,
                            McpEnabled = reg.McpEnabled,
                            McpTransport = reg.McpTransport,
                            LastHeartbeat = lastWrite,
                            IsAlive = true
                        });
                    }
                }
                catch
                {
                    // Ignore corrupted files
                }
            }

            // Clean up runtimes in memory that are no longer present on disk
            _runtimes.RemoveAll(r => !diskPids.Contains(r.ProcessId));
        }
    }

    private void WriteToDisk(RuntimeRegistration reg)
    {
        try
        {
            var path = Path.Combine(_runtimeDir, $"{reg.ProcessId}.json");
            var content = System.Text.Json.JsonSerializer.Serialize(reg, CoordinatorJsonContext.Default.RuntimeRegistration);
            File.WriteAllText(path, content);
        }
        catch
        {
            // Ignore file lock issues during concurrent writes
        }
    }

    private void RemoveFromDisk(int processId)
    {
        try
        {
            var path = Path.Combine(_runtimeDir, $"{processId}.json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Ignore
        }
    }

    public void Register(RuntimeRegistration registration)
    {
        lock (_lock)
        {
            _everRegistered = true;
            ResetShutdownTimer();
            WriteToDisk(registration);

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

            // Touch the file to update its LastWriteTime for other dashboards
            try
            {
                var path = Path.Combine(_runtimeDir, $"{processId}.json");
                if (File.Exists(path))
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                else
                {
                    var reg = new RuntimeRegistration
                    {
                        ProcessId = runtime.ProcessId,
                        ProjectRoot = runtime.ProjectRoot,
                        DataDirectory = runtime.DataDirectory,
                        StartTime = runtime.StartTime,
                        McpEnabled = runtime.McpEnabled,
                        McpTransport = runtime.McpTransport
                    };
                    WriteToDisk(reg);
                }
            }
            catch {}

            return true;
        }
    }

    public bool Unregister(int processId)
    {
        lock (_lock)
        {
            RemoveFromDisk(processId);
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
        SyncFromDisk();
        lock (_lock)
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<RuntimeInfo>();

            // Prioritize most recent runtime per project root first
            foreach (var runtime in _runtimes.Where(r => r.IsAlive).OrderByDescending(r => r.LastHeartbeat))
            {
                var normalizedRoot = Path.GetFullPath(runtime.ProjectRoot);
                
                // Exclude User Profile Home and Global Data Directory from project-level runtime list
                // (they are represented by the dedicated "🌐 Global" button)
                var isUserHome = !string.IsNullOrWhiteSpace(userHome) &&
                    string.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), userHome.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
                var isGlobalScope = string.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), _userScope.GlobalDataDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

                if (isUserHome || isGlobalScope) continue;

                if (seenRoots.Add(normalizedRoot))
                {
                    result.Add(runtime);
                }
            }

            // Sort alphabetically by Project Folder Name for stable & clean UI order
            return result
                .OrderBy(r => Path.GetFileName(Path.TrimEndingDirectorySeparator(r.ProjectRoot)), StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
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
                .FirstOrDefault();

            var directory = latest?.DataDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), ".eling");
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

    public IMemoryService GetGlobalMemoryService()
    {
        lock (_lock)
        {
            if (_globalService is not null) return _globalService;
            _globalService = new MemoryService(
                new FileSystemMemoryStorage(_userScope.GlobalDataDirectory),
                new SqliteMemoryIndex(Path.Combine(_userScope.GlobalDataDirectory, "index.db")));
            return _globalService;
        }
    }

    public IMemoryService? TryResolveMemoryServiceByProjectRoot(string projectRoot)
    {
        lock (_lock)
        {
            var runtime = _runtimes.FirstOrDefault(r =>
                r.IsAlive && string.Equals(Path.GetFullPath(r.ProjectRoot), Path.GetFullPath(projectRoot), StringComparison.OrdinalIgnoreCase));
            if (runtime is null) return null;
            if (!_memoryByDataDirectory.TryGetValue(runtime.DataDirectory, out var service))
            {
                service = new MemoryService(
                    new FileSystemMemoryStorage(runtime.DataDirectory),
                    new SqliteMemoryIndex(Path.Combine(runtime.DataDirectory, "index.db")));
                _memoryByDataDirectory[runtime.DataDirectory] = service;
            }
            return service;
        }
    }

    public IMemoryService? TryResolveMemoryServiceByDataDirectory(string dataDirectory)
    {
        lock (_lock)
        {
            var runtime = _runtimes.FirstOrDefault(r =>
                r.IsAlive && string.Equals(Path.GetFullPath(r.DataDirectory), Path.GetFullPath(dataDirectory), StringComparison.OrdinalIgnoreCase));
            if (runtime is null) return null;
            if (!_memoryByDataDirectory.TryGetValue(runtime.DataDirectory, out var service))
            {
                service = new MemoryService(
                    new FileSystemMemoryStorage(runtime.DataDirectory),
                    new SqliteMemoryIndex(Path.Combine(runtime.DataDirectory, "index.db")));
                _memoryByDataDirectory[runtime.DataDirectory] = service;
            }
            return service;
        }
    }

    public IReadOnlyList<(RuntimeInfo Runtime, IMemoryService Service)> GetAliveProjectServices()
    {
        lock (_lock)
        {
            var result = new List<(RuntimeInfo, IMemoryService)>();
            var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var globalDataDir = Path.GetFullPath(_userScope.GlobalDataDirectory);

            foreach (var runtime in _runtimes.Where(r => r.IsAlive))
            {
                // Skip runtimes that map to the global data directory — those
                // are global-only sessions, not project sessions.
                var runtimeDataDir = Path.GetFullPath(runtime.DataDirectory);
                if (string.Equals(runtimeDataDir, globalDataDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenDirectories.Add(runtimeDataDir)) continue;

                if (!_memoryByDataDirectory.TryGetValue(runtimeDataDir, out var service))
                {
                    service = new MemoryService(
                        new FileSystemMemoryStorage(runtimeDataDir),
                        new SqliteMemoryIndex(Path.Combine(runtimeDataDir, "index.db")));
                    _memoryByDataDirectory[runtimeDataDir] = service;
                }
                result.Add((runtime, service));
            }

            if (result.Count == 0)
            {
                var localDataDir = Path.Combine(Directory.GetCurrentDirectory(), ".eling");
                if (Directory.Exists(localDataDir))
                {
                    if (!_memoryByDataDirectory.TryGetValue(localDataDir, out var localService))
                    {
                        localService = new MemoryService(
                            new FileSystemMemoryStorage(localDataDir),
                            new SqliteMemoryIndex(Path.Combine(localDataDir, "index.db")));
                        _memoryByDataDirectory[localDataDir] = localService;
                    }
                    var syntheticRuntime = new RuntimeInfo
                    {
                        ProcessId = Environment.ProcessId,
                        ProjectRoot = Directory.GetCurrentDirectory(),
                        DataDirectory = localDataDir,
                        StartTime = DateTimeOffset.UtcNow,
                        McpEnabled = true,
                        McpTransport = "stdio",
                        LastHeartbeat = DateTimeOffset.UtcNow,
                        IsAlive = true
                    };
                    result.Add((syntheticRuntime, localService));
                }
            }

            return result.AsReadOnly();
        }
    }

    public async Task<IReadOnlyCollection<ScopedMemory>> ListAggregatedAsync(MemoryStatus? status = null)
    {
        var globalService = GetGlobalMemoryService();
        var globalMemories = await globalService.ListAllAsync();
        if (status.HasValue) globalMemories = globalMemories.Where(m => m.Status == status.Value).ToList();

        var allProjectMemories = new List<ScopedMemory>();
        foreach (var (runtime, service) in GetAliveProjectServices())
        {
            var list = await service.ListAllAsync();
            if (status.HasValue) list = list.Where(m => m.Status == status.Value).ToList();
            foreach (var m in list)
            {
                allProjectMemories.Add(new ScopedMemory(m, MemoryScopeKind.Project, runtime.ProjectRoot));
            }
        }

        var globalScoped = globalMemories.Select(m => new ScopedMemory(m, MemoryScopeKind.Global, null)).ToList();

        // Build distinct list by Id.Value (ULID is globally-unique per design).
        // Two entries with the same Id are the same memory even if they originate
        // from different runtime paths (e.g. global + project fallback).
        var seenKeys = new HashSet<string>();
        var result = new List<ScopedMemory>();

        foreach (var item in globalScoped)
        {
            if (seenKeys.Add(item.Id.Value)) result.Add(item);
        }

        foreach (var item in allProjectMemories)
        {
            if (seenKeys.Add(item.Id.Value)) result.Add(item);
        }

        // Sort descending: newest (by UpdatedAt / CreatedAt) always at the top
        return result
            .OrderByDescending(s => s.Memory.UpdatedAt)
            .ThenByDescending(s => s.Memory.CreatedAt)
            .ToList()
            .AsReadOnly();
    }

    public async Task<IReadOnlyCollection<ScopedSearchResult>> SearchAggregatedAsync(string query, int? limit = null)
    {
        var globalService = GetGlobalMemoryService();
        var globalResults = await globalService.SearchAsync(query);
        var all = new List<ScopedSearchResult>();
        foreach (var r in globalResults)
        {
            all.Add(new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Global, null));
        }

        foreach (var (runtime, service) in GetAliveProjectServices())
        {
            var projectResults = await service.SearchAsync(query);
            foreach (var r in projectResults)
            {
                // Project priority boost
                var boosted = r.Rank - 1000.0;
                all.Add(new ScopedSearchResult(r.Id, boosted, MemoryScopeKind.Project, runtime.ProjectRoot));
            }
        }

        var ordered = all.OrderBy(x => x.Rank).ToList();
        if (limit.HasValue && limit.Value > 0 && ordered.Count > limit.Value)
        {
            ordered = ordered.Take(limit.Value).ToList();
        }

        // Dedup by scoped identity
        var seen = new HashSet<string>();
        var deduped = new List<ScopedSearchResult>();
        foreach (var item in ordered)
        {
            var key = $"{item.Scope}:{item.Id.Value}:{item.ProjectRoot}";
            if (seen.Add(key)) deduped.Add(item);
        }
        return deduped.AsReadOnly();
    }

    public IMemoryMerger Merger => _merger;
    public UserScope UserScope => _userScope;

    private void Sweep()
    {
        bool anyAlive;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            
            // Clean up disk files first
            if (Directory.Exists(_runtimeDir))
            {
                var files = Directory.GetFiles(_runtimeDir, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(file);
                        if (now - lastWrite > _staleAfter)
                        {
                            File.Delete(file);
                        }
                    }
                    catch {}
                }
            }

            foreach (var runtime in _runtimes.Where(r => r.IsAlive && now - r.LastHeartbeat > _staleAfter))
            {
                runtime.IsAlive = false;
                _logger.LogWarning("Runtime stale: pid={Pid} root={Root}", runtime.ProcessId, runtime.ProjectRoot);
                RemoveFromDisk(runtime.ProcessId);
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
