using Eling.Core;
using Eling.Core.Memory;
using Eling.Core.Memory.Storage;
using Eling.Core.Projects;
using Eling.Core.Scope;
using CoordinatorJsonContext = Eling.Core.Runtime.CoordinatorJsonContext;
using RuntimeInfo = Eling.Core.Runtime.RuntimeInfo;

namespace Eling.Backend;

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
    private readonly MemoryChangeBroadcaster? _broadcaster;
    private readonly IWorkspacesRegistry? _workspaces;

    public RuntimeRegistry(
        ILogger<RuntimeRegistry> logger,
        UserScope? userScope = null,
        MemoryChangeBroadcaster? broadcaster = null,
        IWorkspacesRegistry? workspaces = null)
    {
        _logger = logger;
        _broadcaster = broadcaster;
        _workspaces = workspaces;
        _userScope = userScope ?? UserScope.Resolve(Environment.GetEnvironmentVariable("ELING_USER_SCOPE"));
        _runtimeDir = _userScope.RuntimeDirectory;
        Directory.CreateDirectory(_runtimeDir);

        // Intervals are env-tunable so lifecycle tests don't wait real minutes.
        _staleAfter = FromEnvironment("ELING_TEST_STALE_MS", TimeSpan.FromMinutes(5));
        _removeGrace = FromEnvironment("ELING_TEST_GRACE_MS", TimeSpan.FromMinutes(10));
        _emptyShutdownDebounce = FromEnvironment("ELING_TEST_SHUTDOWN_DEBOUNCE_MS", TimeSpan.FromSeconds(30));
        var sweepPeriod = FromEnvironment("ELING_TEST_SWEEP_MS", TimeSpan.FromSeconds(15));
        _sweeper = new Timer(_ => Sweep(), null, sweepPeriod, sweepPeriod);

        // Load existing runtime files on start to sync across dashboard processes
        SyncFromDisk();
    }

    /// <summary>
    /// Adopts live runtimes registered by peer processes.
    /// </summary>
    /// <remarks>
    /// The <c>{pid}.json</c> files in the runtime directory are the only channel
    /// through which processes learn about each other: a peer registers in its
    /// own in-process registry and never calls the owner. The workspace catalogue
    /// cannot replace this — it stores folders, not processes, and outlives them.
    /// </remarks>
    private void SyncFromDisk()
    {
        lock (_lock)
        {
            if (!Directory.Exists(_runtimeDir)) return;
            var now = DateTimeOffset.UtcNow;
            var diskPids = new HashSet<int>();

            foreach (var file in Directory.GetFiles(_runtimeDir, "*.json"))
            {
                try
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid)) continue;

                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (now - lastWrite > _staleAfter)
                    {
                        if (!IsProcessAlive(pid))
                        {
                            File.Delete(file);
                            continue;
                        }

                        lastWrite = DateTime.UtcNow;
                        try { File.SetLastWriteTimeUtc(file, lastWrite); }
                        catch { /* Locked by its owner, which is alive; the touch is only an optimisation. */ }
                    }

                    diskPids.Add(pid);
                    var reg = System.Text.Json.JsonSerializer.Deserialize(
                        File.ReadAllText(file),
                        CoordinatorJsonContext.Default.RuntimeRegistration);
                    if (reg is null) continue;

                    _everRegistered = true;
                    var existing = _runtimes.FirstOrDefault(r => r.ProcessId == reg.ProcessId);
                    if (existing is null)
                    {
                        existing = new RuntimeInfo { ProcessId = reg.ProcessId };
                        _runtimes.Add(existing);
                    }

                    existing.HeadScopeRoot = reg.HeadScopeRoot;
                    existing.WorkspaceRoot = reg.WorkspaceRoot;
                    existing.CodebaseEnabled = reg.CodebaseEnabled;
                    existing.DataDirectory = reg.DataDirectory;
                    existing.StartTime = reg.StartTime;
                    existing.McpEnabled = reg.McpEnabled;
                    existing.McpTransport = reg.McpTransport;
                    existing.LastHeartbeat = lastWrite;
                    existing.IsAlive = true;
                }
                catch
                {
                    // A file being written or deleted concurrently is retried on the next sync.
                }
            }

            // Drop runtimes whose file is gone, but never this backend's own entry.
            _runtimes.RemoveAll(r => r.ProcessId != Environment.ProcessId && !diskPids.Contains(r.ProcessId));
        }
    }

    private void WriteToDisk(Core.Runtime.RuntimeRegistration reg)
    {
        try
        {
            var path = Path.Combine(_runtimeDir, $"{reg.ProcessId}.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(reg, CoordinatorJsonContext.Default.RuntimeRegistration));
        }
        catch
        {
            // A concurrent writer holds the file; the next heartbeat rewrites it.
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
            // Left behind files are swept once their process is gone.
        }
    }

    /// <summary>
    /// Touches the runtime's <c>{pid}.json</c> so peer dashboards, which judge
    /// liveness by its last write time, keep seeing it. Recreates the file if a
    /// sweep removed it.
    /// </summary>
    private void RefreshHeartbeatFile(RuntimeInfo runtime)
    {
        try
        {
            var path = Path.Combine(_runtimeDir, $"{runtime.ProcessId}.json");
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return;
            }

            WriteToDisk(new Core.Runtime.RuntimeRegistration
            {
                ProcessId = runtime.ProcessId,
                HeadScopeRoot = runtime.HeadScopeRoot,
                WorkspaceRoot = runtime.WorkspaceRoot,
                CodebaseEnabled = runtime.CodebaseEnabled,
                DataDirectory = runtime.DataDirectory,
                StartTime = runtime.StartTime,
                McpEnabled = runtime.McpEnabled,
                McpTransport = runtime.McpTransport
            });
        }
        catch
        {
            // A concurrent writer holds the file; the next heartbeat retries.
        }
    }

    /// <summary>
    /// Mirrors a registration into the durable workspace catalogue, keyed by the
    /// workspace folder. The row outlives the process that wrote it, which is the
    /// whole reason the store exists: a codebase index stays readable long after
    /// its process exits, and without this a closed workspace could not be found.
    /// </summary>
    private void RecordProject(Core.Runtime.RuntimeRegistration registration)
    {
        if (_workspaces is null) return;

        // Registrations written by older binaries have no workspaceRoot. Falling
        // back to HeadScopeRoot mirrors RuntimeInfo.CodebaseRoot() and, more
        // importantly, avoids resolving an empty string to this backend's own
        // working directory — which would attribute the wrong workspace.
        var workspace = registration.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspace)) workspace = registration.HeadScopeRoot;
        if (string.IsNullOrWhiteSpace(workspace)) return;
        if (string.Equals(workspace, "UserScope", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(registration.HeadScopeRoot, "UserScope", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (ElingPaths.IsCodebaseExcluded(workspace)) return;

        var now = DateTimeOffset.UtcNow;
        _workspaces.Record(new RegisteredWorkspace(
            string.Empty,
            workspace,
            registration.HeadScopeRoot,
            registration.CodebaseEnabled,
            now,
            now));
    }

    /// <summary>
    /// Records every workspace in this runtime's scope chain, not just the head.
    /// </summary>
    /// <remarks>
    /// A registration only names the chain head, so recording just that would
    /// leave ancestors out of the catalogue — and an ancestor is a scope the
    /// runtime can genuinely write to. The chain is the same walk
    /// <see cref="ScopeChain"/> performs elsewhere, so nothing here is a second
    /// opinion about where a scope lives.
    /// </remarks>
    private void RecordChainWorkspaces(Core.Runtime.RuntimeRegistration registration)
    {
        if (_workspaces is null) return;
        if (string.IsNullOrWhiteSpace(registration.HeadScopeRoot)) return;
        if (string.Equals(registration.HeadScopeRoot, "UserScope", StringComparison.OrdinalIgnoreCase)) return;

        List<ProjectScope> chain;
        try
        {
            chain = ScopeChain.Discover(registration.HeadScopeRoot).Levels.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not discover the scope chain for {Root}.", registration.HeadScopeRoot);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var level in chain)
        {
            if (ElingPaths.IsCodebaseExcluded(level.Root)) continue;

            _workspaces.Record(new RegisteredWorkspace(
                string.Empty,
                level.Root,
                level.Root,
                registration.CodebaseEnabled,
                now,
                now));
        }
    }

    /// <summary>
    /// Every workspace in the catalogue, live or not. Empty when no store is
    /// attached, which is the same shape as an empty store rather than an error —
    /// the dashboard can render without it.
    /// </summary>
    public async Task<IReadOnlyList<RegisteredWorkspace>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        if (_workspaces is null) return Array.Empty<RegisteredWorkspace>();
        return await _workspaces.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers a process: adds it to the live list and mirrors it, plus every
    /// <c>.eling</c> in its chain, into the durable workspace catalogue.
    /// </summary>
    public void Register(Core.Runtime.RuntimeRegistration registration)
    {
        lock (_lock)
        {
            _everRegistered = true;
            ResetShutdownTimer();
            WriteToDisk(registration);
            RecordProject(registration);
            RecordChainWorkspaces(registration);

            var existing = _runtimes.FirstOrDefault(r => r.ProcessId == registration.ProcessId);
            if (existing is not null)
            {
                existing.HeadScopeRoot = registration.HeadScopeRoot;
                existing.WorkspaceRoot = registration.WorkspaceRoot;
                existing.CodebaseEnabled = registration.CodebaseEnabled;
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
                HeadScopeRoot = registration.HeadScopeRoot,
                WorkspaceRoot = registration.WorkspaceRoot,
                CodebaseEnabled = registration.CodebaseEnabled,
                DataDirectory = registration.DataDirectory,
                StartTime = registration.StartTime,
                McpEnabled = registration.McpEnabled,
                McpTransport = registration.McpTransport,
                LastHeartbeat = DateTimeOffset.UtcNow,
                IsAlive = true
            });

            _logger.LogInformation(
                "Runtime registered: pid={Pid} root={Root}",
                registration.ProcessId, registration.HeadScopeRoot);
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

            RefreshHeartbeatFile(runtime);
            RefreshWorkspaceRow(runtime);

            return true;
        }
    }

    /// <summary>
    /// Touches the catalogue row so peer dashboards reading the same store see a
    /// workspace as recently active. Re-records rather than just bumping a
    /// timestamp: the upsert is idempotent and also repairs a row whose stored
    /// fields drifted from what the live registration says.
    /// </summary>
    private void RefreshWorkspaceRow(RuntimeInfo runtime)
    {
        _workspaces?.Record(new RegisteredWorkspace(
            string.Empty,
            string.IsNullOrWhiteSpace(runtime.WorkspaceRoot) ? runtime.HeadScopeRoot : runtime.WorkspaceRoot,
            runtime.HeadScopeRoot,
            runtime.CodebaseEnabled,
            runtime.StartTime,
            DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Removes a process from the live list. Nothing is deleted from the
    /// catalogue: the workspace may still be indexed and searchable, which is the
    /// reason this store exists at all.
    /// </summary>
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

    /// <summary>
    /// Removes a workspace from the durable catalogue, and if a live instance
    /// is active for that workspace, unregisters it.
    /// </summary>
    public async Task<bool> DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return false;
        var normalized = NormalizeRoot(workspaceRoot);

        lock (_lock)
        {
            var matching = _runtimes.Where(r => string.Equals(NormalizeRoot(r.WorkspaceRoot), normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var r in matching)
            {
                Unregister(r.ProcessId);
            }
        }

        if (_workspaces is null) return false;
        return await _workspaces.DeleteByRootAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam: writes rows into the catalogue only. The federated read path
    /// (<see cref="AliveOrRegisteredAsync"/>) reads that store directly, so
    /// recording is enough to exercise a closed workspace — and deliberately does
    /// not touch the in-memory list, which stays the live-process view.
    /// </summary>
    internal void RecordWorkspacesForTest(IReadOnlyList<RegisteredWorkspace> rows)
    {
        if (_workspaces is null) return;

        foreach (var row in rows)
        {
            _workspaces.Record(row);
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
                var normalizedRoot = Path.GetFullPath(runtime.HeadScopeRoot);

                if (!IsProjectScopedRoot(normalizedRoot)) continue;

                if (seenRoots.Add(normalizedRoot))
                {
                    result.Add(runtime);
                }
            }

            // Sort alphabetically by Project Folder Name for stable & clean UI order
            return result
                .OrderBy(r => Path.GetFileName(Path.TrimEndingDirectorySeparator(r.HeadScopeRoot)), StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// The union of <see cref="Alive"/> and every workspace in the durable
    /// project registry, so a project that is closed but still indexed appears
    /// in the codebase page's project list.
    /// </summary>
    /// <remarks>
    /// The shape is deliberately identical to <see cref="Alive"/> — same
    /// <see cref="RuntimeInfo"/> fields, same filters, same ordering — so the
    /// frontend needs no special case to consume it. A registered-but-dead
    /// project is reported with <c>ProcessId = 0</c> and <c>IsAlive = false</c>;
    /// live processes always win the dedupe, so a running project's row carries
    /// its real pid and heartbeat.
    /// <para>
    /// Memory deliberately does NOT use this. A memory service is a live
    /// resource: the workspace may not even exist on disk any more, and its
    /// <c>.eling</c> is only meaningful while something owns it. Codebase
    /// indexes are read-only files in a global store, so a closed project's
    /// index is still fully readable — which is exactly the difference between
    /// the two subdomains.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<RuntimeInfo>> AliveOrRegisteredAsync(
        CancellationToken cancellationToken = default)
    {
        var live = Alive();
        if (_workspaces is null) return live;

        var remembered = await _workspaces.ListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<RuntimeInfo>(live);

        // Same identity Alive() uses: the workspace folder, case-insensitively.
        var seenRoots = new HashSet<string>(
            live.Select(r => NormalizeRoot(r.WorkspaceRoot)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in remembered)
        {
            if (string.IsNullOrWhiteSpace(row.WorkspaceRoot)) continue;
            if (!IsProjectScopedRoot(row.WorkspaceRoot)) continue;
            // A workspace already covered by a live runtime keeps that entry: it
            // carries the current pid and heartbeat. Add returns false when the
            // root was already present.
            if (!seenRoots.Add(NormalizeRoot(row.WorkspaceRoot))) continue;
            result.Add(ToRuntimeInfo(row));
        }

        return result
            .OrderBy(r => Path.GetFileName(Path.TrimEndingDirectorySeparator(r.WorkspaceRoot)), StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// A catalogue row shaped like a runtime entry, with the fields the codebase
    /// page reads already filled in. A remembered workspace has no live process,
    /// so <see cref="RuntimeInfo.ProcessId"/> is 0 — the marker the dashboard
    /// uses to tell "remembered" from "running".
    /// </summary>
    private static RuntimeInfo ToRuntimeInfo(RegisteredWorkspace row)
    {
        var workspace = Path.GetFullPath(row.WorkspaceRoot);
        return new RuntimeInfo
        {
            ProcessId = 0,
            HeadScopeRoot = workspace,
            WorkspaceRoot = workspace,
            CodebaseEnabled = row.CodebaseEnabled,
            DataDirectory = Path.Combine(workspace, ProjectScope.DataDirectoryName),
            McpEnabled = false,
            McpTransport = "none",
            StartTime = row.FirstSeenAt,
            LastHeartbeat = row.LastSeenAt,
            IsAlive = false,
        };
    }

    /// <summary>
    /// The three exclusions <see cref="Alive"/> applies, factored out so both
    /// paths agree: user home, the global data directory, and the
    /// <c>UserScope</c> sentinel. A registry row bypassed none of them —
    /// the workspace could have been user home when it registered.
    /// </summary>
    private bool IsProjectScopedRoot(string root)
    {
        if (ElingPaths.IsCodebaseExcluded(root))
        {
            return false;
        }

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userHome) &&
            string.Equals(NormalizeRoot(root), NormalizeRoot(userHome), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(NormalizeRoot(root), NormalizeRoot(_userScope.GlobalDataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(root, "UserScope", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(NormalizeRoot(root)), "UserScope", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeRoot(string root)
    {
        try
        {
            return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed row must not break the whole listing; it just cannot
            // be matched against a live root either.
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
                    new SqliteMemoryIndex(Path.Combine(directory, "memory.db")));
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
                new SqliteMemoryIndex(Path.Combine(_userScope.GlobalDataDirectory, "memory.db")));
            return _globalService;
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
                    new SqliteMemoryIndex(Path.Combine(runtime.DataDirectory, "memory.db")));
                _memoryByDataDirectory[runtime.DataDirectory] = service;
            }

            return service;
        }
    }

    /// <summary>
    /// A memory service for a project root, whether or not a process is alive.
    /// </summary>
    /// <remarks>
    /// Falls back to reading the root's <c>.eling</c> directly. A memory service
    /// is just storage plus an index over a directory, so nothing about reading
    /// needs a live process — the registry was needed to manage that lifetime,
    /// not to perform IO. Without this, a scope whose last session has closed
    /// could be written to through MCP and then be invisible to the dashboard,
    /// which is the reverse of what "a closed project stays findable" means.
    /// </remarks>
    public IMemoryService? TryResolveMemoryServiceByScopeRoot(string scopeRoot)
    {
        lock (_lock)
        {
            var runtime = _runtimes.FirstOrDefault(r =>
                r.IsAlive && string.Equals(Path.GetFullPath(r.HeadScopeRoot), Path.GetFullPath(scopeRoot), StringComparison.OrdinalIgnoreCase));
            if (runtime is not null && TryGetMemoryService(runtime.DataDirectory, out var live))
            {
                return live;
            }

            var dataDirectory = Path.Combine(Path.GetFullPath(scopeRoot), ProjectScope.DataDirectoryName);
            if (!Directory.Exists(dataDirectory)) return null;
            return TryGetMemoryService(dataDirectory, out var onDisk) ? onDisk : null;
        }
    }

    private bool TryGetMemoryService(string dataDirectory, out IMemoryService service)
    {
        if (_memoryByDataDirectory.TryGetValue(dataDirectory, out var existing))
        {
            service = existing;
            return true;
        }

        service = new MemoryService(
            new FileSystemMemoryStorage(dataDirectory),
            new SqliteMemoryIndex(Path.Combine(dataDirectory, "memory.db")));
        _memoryByDataDirectory[dataDirectory] = service;
        return true;
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
                        new SqliteMemoryIndex(Path.Combine(runtimeDataDir, "memory.db")));
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
                            new SqliteMemoryIndex(Path.Combine(localDataDir, "memory.db")));
                        _memoryByDataDirectory[localDataDir] = localService;
                    }

                    var syntheticRuntime = new RuntimeInfo
                    {
                        ProcessId = Environment.ProcessId,
                        HeadScopeRoot = Directory.GetCurrentDirectory(),
                        WorkspaceRoot = Directory.GetCurrentDirectory(),
                        CodebaseEnabled = true,
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

    /// <summary>
    /// A memory service for a canonical project's central local shard
    /// (<c>DATA/eling/projects/&lt;name&gt;-&lt;hash&gt;/</c>), or null when the
    /// shard directory does not exist yet (lazy: no shard until first write).
    /// Worktrees of one repo share one shard via the canonical root.
    /// </summary>
    public IMemoryService? TryResolveLocalServiceByScopeRoot(string scopeRoot, out string? canonicalRoot, bool createIfMissing = false)
    {
        canonicalRoot = CanonicalProjectRoot.Resolve(scopeRoot);
        var localDir = ElingPaths.ResolveProjectLocalDir(canonicalRoot);
        lock (_lock)
        {
            if (!createIfMissing && !Directory.Exists(localDir)) return null;
            if (!_memoryByDataDirectory.TryGetValue(localDir, out var service))
            {
                service = new MemoryService(
                    new FileSystemMemoryStorage(localDir),
                    new SqliteMemoryIndex(Path.Combine(localDir, "memory.db")));
                _memoryByDataDirectory[localDir] = service;
            }

            return service;
        }
    }

    public async Task<IReadOnlyCollection<ScopedMemory>> ListAggregatedAsync(MemoryStatus? status = null)
    {
        var globalService = GetGlobalMemoryService();
        var globalMemories = await globalService.ListAllAsync();
        if (status.HasValue) globalMemories = globalMemories.Where(m => m.Status == status.Value).ToList();

        var allLocalMemories = new List<ScopedMemory>();
        var allProjectMemories = new List<ScopedMemory>();
        foreach (var (runtime, service) in GetAliveProjectServices())
        {
            var list = await service.ListAllAsync();
            if (status.HasValue) list = list.Where(m => m.Status == status.Value).ToList();
            foreach (var m in list)
            {
                allProjectMemories.Add(new ScopedMemory(m, MemoryScopeKind.Project, runtime.HeadScopeRoot));
            }

            var localService = TryResolveLocalServiceByScopeRoot(runtime.HeadScopeRoot, out var canonicalRoot);
            if (localService is not null)
            {
                var locals = await localService.ListAllAsync();
                if (status.HasValue) locals = locals.Where(m => m.Status == status.Value).ToList();
                foreach (var m in locals)
                {
                    allLocalMemories.Add(new ScopedMemory(m, MemoryScopeKind.ProjectLocal, canonicalRoot));
                }
            }
        }

        var globalScoped = globalMemories.Select(m => new ScopedMemory(m, MemoryScopeKind.Global, null)).ToList();

        // Build distinct list by Id.Value (ULID is globally-unique per design).
        // Two entries with the same Id are the same memory even if they originate
        // from different runtime paths (e.g. global + project fallback).
        // Project-local wins ties (merge order local > project > global).
        var seenKeys = new HashSet<string>();
        var result = new List<ScopedMemory>();

        foreach (var item in allLocalMemories)
        {
            if (seenKeys.Add(item.Id.Value)) result.Add(item);
        }

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
                all.Add(new ScopedSearchResult(r.Id, boosted, MemoryScopeKind.Project, runtime.HeadScopeRoot));
            }

            var localService = TryResolveLocalServiceByScopeRoot(runtime.HeadScopeRoot, out var canonicalRoot);
            if (localService is not null)
            {
                // Project-local outranks project (merge order local > project > global)
                var localResults = await localService.SearchAsync(query);
                foreach (var r in localResults)
                {
                    var boostedLocal = r.Rank - 2000.0;
                    all.Add(new ScopedSearchResult(r.Id, boostedLocal, MemoryScopeKind.ProjectLocal, canonicalRoot));
                }
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

            // Self-heartbeat: this process keeps its own registration alive so
            // the shared registry never sweeps a live backend. Restores the
            // pre-consolidation coordinator behavior ("coordinator never marks
            // itself stale"); the disk file refresh lets peer dashboards keep
            // seeing this instance via SyncFromDisk.
            var self = _runtimes.FirstOrDefault(r => r.ProcessId == Environment.ProcessId);
            if (self is not null)
            {
                self.LastHeartbeat = now;
                self.IsAlive = true;
                RefreshHeartbeatFile(self);
                RefreshWorkspaceRow(self);
            }

            // Clean up disk files first
            if (Directory.Exists(_runtimeDir))
            {
                var files = Directory.GetFiles(_runtimeDir, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        if (!int.TryParse(fileName, out var pid)) continue;

                        var lastWrite = File.GetLastWriteTimeUtc(file);
                        if (now - lastWrite > _staleAfter)
                        {
                            if (IsProcessAlive(pid))
                            {
                                // OS process is still alive; do not delete, refresh file timestamp
                                try
                                {
                                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                                }
                                catch
                                {
                                    // Ignore lock issues
                                }
                            }
                            else
                            {
                                File.Delete(file);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            // Prune the in-memory list of runtimes whose heartbeat went stale. Nothing is
            // written to the catalogue here: its rows describe folders, and a
            // folder outlives the process by design.
            var prunedAny = false;
            foreach (var runtime in _runtimes.Where(r => r.IsAlive && now - r.LastHeartbeat > _staleAfter))
            {
                if (IsProcessAlive(runtime.ProcessId))
                {
                    runtime.LastHeartbeat = now;
                    runtime.IsAlive = true;
                    RefreshHeartbeatFile(runtime);
                    RefreshWorkspaceRow(runtime);
                }
                else
                {
                    runtime.IsAlive = false;
                    _logger.LogWarning("Runtime stale: pid={Pid} root={Root}", runtime.ProcessId, runtime.HeadScopeRoot);
                    prunedAny = true;
                }
            }

            if (prunedAny)
            {
                _broadcaster?.Notify("runtimes");
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

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Process does not exist
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has exited
            return false;
        }
        catch
        {
            // Access denied or other OS error - safely assume alive to avoid accidental deletion
            return true;
        }
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