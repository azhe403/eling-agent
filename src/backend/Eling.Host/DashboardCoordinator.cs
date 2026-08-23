using Eling.Core;
using Eling.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

// ===========================================================================
// Dashboard Coordinator — User-scoped, single instance on 127.0.0.1:4317
// ===========================================================================

public sealed class DashboardCoordinator : IAsyncDisposable
{
    private readonly UserScope _userScope;
    private readonly ILogger<DashboardCoordinator> _logger;
    private readonly object _lock = new();
    private Timer? _heartbeatTimer;
    private readonly List<RuntimeInfo> _runtimes = [];
    private bool _isCoordinator = false;
    private WebApplication? _app;
    private CancellationTokenSource? _shutdownCts;

    public DashboardCoordinator(UserScope userScope, ILogger<DashboardCoordinator> logger)
    {
        _userScope = userScope;
        _logger = logger;
    }

    public async Task<bool> TryStartOrRegisterAsync(RuntimeRegistration registration, WebApplication app, bool noDashboard)
    {
        var coordinationFile = Program.GetRuntimeCoordinationFile(_userScope);
        var registryFile = Program.GetRuntimeRegistryFile(_userScope);

        var claim = new CoordinatorClaim
        {
            ProcessId = registration.ProcessId,
            StartedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            // Try to atomically claim coordinator role via file
            var claimJson = JsonSerializer.Serialize(claim, CoordinatorJsonContext.Default.CoordinatorClaim);

            // If file exists and process is alive, we are NOT the coordinator
            if (File.Exists(coordinationFile))
            {
                var existingClaim = await SafeReadCoordinatorClaimAsync(coordinationFile);
                if (existingClaim is not null && IsProcessAlive(existingClaim.ProcessId) && existingClaim.ProcessId != registration.ProcessId)
                {
                    _logger.LogDebug("Existing coordinator {Pid} is alive, registering with it", existingClaim.ProcessId);
                    _app = app;
                    await RegisterWithCoordinatorAsync(registration);
                    return false;
                }
            }

            // Claim coordinator role
            await File.WriteAllTextAsync(coordinationFile, claimJson);
            _isCoordinator = true;
            _app = app;
            _logger.LogInformation("Claimed coordinator role (PID {Pid})", registration.ProcessId);

            // Start heartbeat timer
            _shutdownCts = new CancellationTokenSource();
            _heartbeatTimer = new Timer(
                _ => CheckHeartbeats(_shutdownCts.Token),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10));

            RegisterRuntime(registration);

            if (!noDashboard)
            {
                _logger.LogInformation("Dashboard: http://localhost:4317/dashboard");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to claim coordinator role, registering with existing");
            _app = app;
            await RegisterWithCoordinatorAsync(registration);
            return false;
        }
    }

    private void RegisterRuntime(RuntimeRegistration registration)
    {
        var info = new RuntimeInfo
        {
            ProcessId = registration.ProcessId,
            ProjectRoot = registration.ProjectRoot,
            DataDirectory = registration.DataDirectory,
            StartTime = registration.StartTime,
            McpEnabled = registration.McpEnabled,
            McpTransport = registration.McpTransport,
            LastHeartbeat = DateTimeOffset.UtcNow,
        };

        lock (_lock)
        {
            _runtimes.Add(info);
        }

        PersistRuntimes();
    }

    private async Task RegisterWithCoordinatorAsync(RuntimeRegistration registration)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var json = JsonSerializer.Serialize(registration, CoordinatorJsonContext.Default.RuntimeRegistration);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("http://localhost:4317/api/coordinator/register", content);
            response.EnsureSuccessStatusCode();
            _logger.LogDebug("Registered with coordinator");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not register with coordinator (not running?)");
        }
    }

    public async Task UnregisterAsync(int pid)
    {
        lock (_lock)
        {
            _runtimes.RemoveAll(r => r.ProcessId == pid);
        }

        PersistRuntimes();
        await Task.CompletedTask;
    }

    private void CheckHeartbeats(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;

            // Self-heartbeat: coordinator never marks itself stale
            var self = _runtimes.FirstOrDefault(r => r.ProcessId == Environment.ProcessId);
            if (self is not null)
            {
                self.LastHeartbeat = now;
                self.IsAlive = true;
            }

            foreach (var runtime in _runtimes)
            {
                if (runtime.IsAlive && (now - runtime.LastHeartbeat).TotalSeconds > 30)
                {
                    runtime.IsAlive = false;
                    _logger.LogDebug("Runtime {Pid} marked stale (no heartbeat for {Seconds:F0}s)",
                        runtime.ProcessId, (now - runtime.LastHeartbeat).TotalSeconds);
                }
            }

            // Remove dead runtimes after 60s grace period
            var before = _runtimes.Count;
            _runtimes.RemoveAll(r => !r.IsAlive && (now - r.LastHeartbeat).TotalSeconds > 60);
            if (_runtimes.Count != before)
            {
                PersistRuntimes();
                _logger.LogDebug("Cleaned {Count} dead runtimes", before - _runtimes.Count);
            }

            // All EXTERNAL runtimes dead → shut down (self excluded; standalone dashboard stays)
            var external = _runtimes.Where(r => r.ProcessId != Environment.ProcessId).ToList();
            if (_isCoordinator && external.Count > 0 && external.All(r => !r.IsAlive))
            {
                _logger.LogInformation("All external runtimes dead, shutting down coordinator");
                _ = ShutdownAsync();
            }
        }
    }

    private void PersistRuntimes()
    {
        try
        {
            var registryFile = Program.GetRuntimeRegistryFile(_userScope);
            lock (_lock)
            {
                var json = JsonSerializer.Serialize(_runtimes, CoordinatorJsonContext.Default.ListRuntimeInfo);
                File.WriteAllText(registryFile, json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist runtimes");
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownCts is not null)
        {
            _shutdownCts.Cancel();
        }

        if (_app is not null)
        {
            await _app.StopAsync();
        }
    }

    private async Task<CoordinatorClaim?> SafeReadCoordinatorClaimAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize(json, CoordinatorJsonContext.Default.CoordinatorClaim);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_heartbeatTimer is not null)
        {
            await _heartbeatTimer.DisposeAsync();
        }

        if (_isCoordinator)
        {
            try
            {
                File.Delete(Program.GetRuntimeCoordinationFile(_userScope));
            }
            catch { /* best-effort cleanup */ }
        }
    }

    // =======================================================================
    // HTTP Endpoints — only mapped on the coordinator instance
    // =======================================================================

    public static void MapCoordinatorEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/coordinator")
            .RequireHost("localhost:4317");

        group.MapPost("/register", (RuntimeRegistration reg, DashboardCoordinator coordinator) =>
        {
            coordinator.RegisterRuntime(reg);
            return Results.Ok(new { status = "registered" });
        });

        group.MapGet("/runtimes", (DashboardCoordinator coordinator) =>
        {
            lock (coordinator._lock)
            {
                return Results.Ok(coordinator._runtimes.Where(r => r.IsAlive).ToList());
            }
        });

        group.MapPost("/heartbeat/{pid}", (int pid, DashboardCoordinator coordinator) =>
        {
            lock (coordinator._lock)
            {
                var runtime = coordinator._runtimes.FirstOrDefault(r => r.ProcessId == pid);
                if (runtime is not null)
                {
                    runtime.LastHeartbeat = DateTimeOffset.UtcNow;
                    runtime.IsAlive = true;
                }
            }
            return Results.Ok();
        });

        group.MapDelete("/unregister/{pid}", (int pid, DashboardCoordinator coordinator) =>
        {
            _ = coordinator.UnregisterAsync(pid);
            return Results.Ok(new { status = "unregistered" });
        });
    }
}


