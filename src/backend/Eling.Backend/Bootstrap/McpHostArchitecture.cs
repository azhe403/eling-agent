using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Eling.Backend.Bootstrap;
using Eling.Backend.Mcp;
using Eling.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend;

// Exposed so `WebApplicationFactory<Program>` (and MemoryApiTests) can
// reference the entry-point type.
public partial class Program;

// ======================================================================
// AppServices — process-wide shared singleton container
// ======================================================================
//
// Built ONCE per process and shared by both the MCP host and the HTTP host.
// The cross-cutting state that needs to be the same object across hosts is
// limited to:
//   - RuntimeRegistry       (in-memory map of runtimes + liveness sweeper)
//   - MemoryChangeBroadcaster (SSE channel for memory mutations)
//   - ILoggerFactory        (Serilog sinks)
//   - UserScope             (already shared by value, included for clarity)
//
// Storage layers (FileSystemMemoryStorage, SqliteMemoryIndex,
// FileSystemIntentionStorage) are file-backed and safe to instantiate
// separately per host: both hosts read/write the same files. Constructing
// them twice is intentional and cheap.
//
public sealed class AppServices : IDisposable
{
    public RuntimeRegistry Registry { get; }
    public MemoryChangeBroadcaster Broadcaster { get; }
    public ILoggerFactory LoggerFactory { get; }
    public UserScope UserScope { get; }
    public ProjectScope ProjectScope { get; }
    public string EffectiveDataDir { get; }
    public bool Disposed { get; private set; }

    private AppServices(
        RuntimeRegistry registry,
        MemoryChangeBroadcaster broadcaster,
        ILoggerFactory loggerFactory,
        UserScope userScope,
        ProjectScope projectScope,
        string effectiveDataDir
    )
    {
        Registry = registry;
        Broadcaster = broadcaster;
        LoggerFactory = loggerFactory;
        UserScope = userScope;
        ProjectScope = projectScope;
        EffectiveDataDir = effectiveDataDir;
    }

    public static AppServices Create(ProjectContext context)
    {
        // Build a minimal Serilog-backed logger factory at this point so the
        // RuntimeRegistry constructor receives a real ILogger. The full
        // Serilog wiring is registered inside DashboardServices (host scope).
        var minimalLoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
        {
        });
        var registry = new RuntimeRegistry(
            minimalLoggerFactory.CreateLogger<RuntimeRegistry>(),
            context.UserScope);
        var broadcaster = new MemoryChangeBroadcaster();

        return new AppServices(
            registry,
            broadcaster,
            minimalLoggerFactory,
            context.UserScope,
            context.ProjectScope,
            context.EffectiveDataDir);
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Registry.Dispose();
        (LoggerFactory as IDisposable)?.Dispose();
    }
}

// ======================================================================
// McpHostBuilder — creates the GenericHost that owns the stdio transport
// and the shared in-memory services. The MCP host NEVER restarts during
// the process lifetime; it just keeps the MCP JSON-RPC server alive and
// the shared singletons available for the HTTP host to reuse.
// ======================================================================
public static class McpHostBuilder
{
    public static IHost Build(AppServices shared, ProjectContext context)
    {
        var builder = Host.CreateDefaultBuilder();

        builder.ConfigureServices(services =>
        {
            // Serilog to file + stderr so stdout stays clean for MCP stdio JSON-RPC.
            services.AddElingLogging(context.EffectiveDataDir);
            services.AddElingCoreServices(context.ProjectScope, context.UserScope);
            services.AddElingMcpServerStdio();

            // Cross-host singletons: register the SAME instance so the HTTP host
            // shares the in-memory registry and broadcaster with MCP.
            // NOTE: do NOT register shared.LoggerFactory here. The factory was
            // built at process start with no providers (it only serves to
            // construct RuntimeRegistry before AddElingLogging is set up).
            // Registering it as a singleton would shadow the Serilog-configured
            // ILoggerFactory added by AddElingLogging above, causing peer-mode
            // processes (which never build a WebApplication) to resolve the
            // empty factory and silently drop all log lines.
            services.AddSingleton(shared.Registry);
            services.AddSingleton(shared.Broadcaster);
            services.AddSingleton<IMemoryChangeNotifier>(_ => shared.Broadcaster);
            services.AddSingleton(shared.UserScope);
        });

        return builder.Build();
    }
}

// ======================================================================
// HttpLoop — port-acquisition loop. Tries to bind Kestrel to
// 127.0.0.1:dashboardPort. On success, runs the host to completion and
// returns. If the port is taken, randomized jitter delay then retry.
// Sibling processes that lose the race stay MCP-only; the winner becomes
// the full-stack instance.
// ======================================================================
public static class HttpLoop
{
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Try to bind the HTTP host. On success, runs the host to completion and
    /// returns 0. On cancellation, returns 0 silently.
    /// </summary>
    public static async Task<int> RunAsync(
        AppServices shared,
        ProjectContext context,
        int dashboardPort,
        bool isDevMode,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var logger = loggerFactory.CreateLogger("Eling.Backend.HttpLoop");
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            WebApplication? app = null;
            try
            {
                // Check port BEFORE building the WebApplication. If the port
                // is already taken, enter peer polling mode without creating
                // a second WebApplication (and its second MCP hosted service).
                var ownerMode = !DashboardPort.IsLoopbackListening(dashboardPort);
                logger.LogInformation(
                    "HTTP host attempt #{Attempt}: {Mode} 127.0.0.1:{Port}",
                    attempt, ownerMode ? "OWNER, binding Kestrel to" : "peer, polling for", dashboardPort);

                if (!ownerMode)
                {
                    // Peer path: just poll the dashboard port. The peer doesn't
                    // serve HTTP and doesn't build a WebApplication — the MCP
                    // host (mcpHost) is already serving stdio. When the owner
                    // dies and the port frees, we continue the loop and rebuild
                    // as owner.
                    var takeoverInterval = DashboardPort.ResolveTakeoverMs();
                    await PortMonitor.WaitForPortFreeAsync(dashboardPort, takeoverInterval, logger, cancellationToken);
                    logger.LogInformation(
                        "Peer promoting to owner on port {Port} (was waiting {Ms}ms per poll)",
                        dashboardPort, (int)takeoverInterval.TotalMilliseconds);
                    continue; // back to top: rebuild as owner next iteration
                }

                // Owner path: build the WebApplication (Kestrel + routes + self-registration).
                app = BuildWebApplication(shared, context, dashboardPort, isDevMode, loggerFactory);

                // After the owner wins the port, kick off the dashboard dev
                // server. Peers don't spawn because only one FE process should
                // own 4427, and the owner is the process that owns the REST port.
                if (isDevMode)
                {
                    var feLogger = loggerFactory.CreateLogger("Eling.Backend.FrontendDev");
                    TrySpawnPnpmFrontend(context, dashboardPort, feLogger);
                }

                await app.RunAsync(cancellationToken);
                logger.LogInformation("HTTP host shut down cleanly on attempt #{Attempt}", attempt);
                return 0;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                logger.LogWarning(
                    "Port {Port} occupied (attempt #{Attempt}); retrying after jitter",
                    dashboardPort, attempt);
                await DelayWithJitterAsync(cancellationToken);
            }
            catch (IOException ex) when (BindFailure.IsAddressInUse(ex))
            {
                logger.LogWarning(
                    "Port {Port} bound by peer (attempt #{Attempt}, IOException): {Message}",
                    dashboardPort, attempt, ex.Message);
                await DelayWithJitterAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "HTTP host crashed on attempt #{Attempt}", attempt);
                return 1;
            }
            finally
            {
                if (app is not null)
                {
                    try
                    {
                        await app.DisposeAsync();
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Build a WebApplication configured for owner / peer mode. Called from
    /// the retry loop. The caller is responsible for disposing the app.
    /// </summary>
    public static WebApplication BuildWebApplication(
        AppServices shared,
        ProjectContext context,
        int dashboardPort,
        bool isDevMode,
        ILoggerFactory loggerFactory
    )
    {
        var ownerMode = !DashboardPort.IsLoopbackListening(dashboardPort);
        var options = new WebApplicationOptions
        {
            // WebRootPath is only used by UseStaticFiles; owner binds UI, peer doesn't.
            WebRootPath = ownerMode
                ? Path.Combine(AppContext.BaseDirectory, "eling-dashboard-ui")
                : AppContext.BaseDirectory
        };

        var builder = WebApplication.CreateBuilder(options);

        if (!ownerMode)
        {
            // Peer already serves REST + UI on the port (race window between
            // probe and bind). Don't configure any Kestrel listener; the
            // generated WebApplication still hosts services but skips the
            // HTTP socket.
            builder.WebHost.ConfigureKestrel(_ =>
            {
            });
        }
        else
        {
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, dashboardPort);
                k.Listen(IPAddress.IPv6Loopback, dashboardPort);
            });
        }

        // Register the shared services from the MCP host. DashboardServices
        // is still called for the REST-only registrations (ConfigureHttpJsonOptions,
        // IMemoryService factory, etc.) but the shared singletons override
        // anything it would have re-registered.
        DashboardServices.Register(builder.Services, context, shared, ownerMode);

        var app = builder.Build();

        // Only the owner maps HTTP routes. All processes (owner and peers) self-register
        // via mcpHost in Program.cs.
        if (ownerMode)
        {
            DashboardRoutes.Map(app);
        }

        return app;
    }

    private static async Task DelayWithJitterAsync(CancellationToken cancellationToken)
    {
        var jitter = Random.Shared.NextDouble();
        var delay = MinRetryDelay + (MaxRetryDelay - MinRetryDelay) * jitter;
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Propagated to the outer loop.
        }
    }

    // The owner process spawns the dashboard dev server. Implementation lives
    // in FrontendDevSpawner below to keep this file's primary type focused on
    // the port-acquisition loop.
    internal static void TrySpawnPnpmFrontend(
        ProjectContext context,
        int backendPort,
        ILogger logger
    )
        => FrontendDevSpawner.TrySpawn(context, backendPort, logger);
}

// ======================================================================
// PortMonitor — used by MCP-only peers in HttpLoop to wait for the
// dashboard port to become free (after the owner dies), at which point the
// peer can rebuild as owner. Polling cadence is set by ELING_TAKEOVER_MS.
// ======================================================================
internal static class PortMonitor
{
    /// <summary>
    /// Poll <paramref name="port"/> on the configured interval until
    /// <see cref="DashboardPort.IsLoopbackListening"/> returns false or
    /// <paramref name="cancellationToken"/> fires. Returns when the port
    /// is free. Throws <see cref="OperationCanceledException"/> on cancel.
    /// </summary>
    public static async Task WaitForPortFreeAsync(
        int port,
        TimeSpan interval,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        // Cadence policy: keep poll-loop output invisible at default level
        // (Information). Per-tick heartbeat = Trace, periodic heartbeat =
        // Debug. Only the actual state transition (port free → promotion) is
        // an Information event. To diagnose a stuck peer, raise the log level
        // for "Eling.Backend.PortMonitor" to Debug (or lower the default to
        // Debug) and tail `.eling/logs/mcp.log`.
        const long LogEvery = 10;
        var ticks = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!DashboardPort.IsLoopbackListening(port))
            {
                logger.LogInformation(
                    "Dashboard port {Port} is free (poll #{Ticks}); peer will attempt promotion",
                    port, ticks);
                return;
            }

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            ticks++;
            // Per-tick heartbeat — Trace (off in any sane config).
            logger.LogTrace(
                "Peer still polling port {Port} (poll #{Ticks}, interval {IntervalMs}ms)",
                port, ticks, (int)interval.TotalMilliseconds);
            // Periodic heartbeat — Debug (also off by default).
            if (ticks % LogEvery == 0)
            {
                logger.LogDebug(
                    "Peer still waiting on port {Port} (poll #{Ticks}, interval {IntervalMs}ms)",
                    port, ticks, (int)interval.TotalMilliseconds);
            }
        }
    }
}

// ======================================================================
// FrontendDevSpawner — spawns `pnpm dev:frontend` so the dashboard UI
// hot-reloads. The owner process (the one that won the port race) is the
// only process that should spawn; peers stay MCP-only so we never end up
// with two competing dev servers on 4427.
// ======================================================================
internal static class FrontendDevSpawner
{
    public static void TrySpawn(ProjectContext context, int backendPort, ILogger logger)
    {
        var repoRoot = FindRepoRootWithPnpm();
        if (repoRoot is null) return;

        // Pre-spawn cleanup: if 4427 is already listening, kill the owning
        // process tree so the FE always starts from a clean state. This
        // handles zombie FE dev processes left from previous runs.
        if (IsPortListening(4427))
        {
            KillProcessTreeOnPort(4427, logger);
        }

        Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pnpm",
                    Arguments = "dev:frontend",
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false
                };
                psi.EnvironmentVariables["ELING_BACKEND_PORT"] = backendPort.ToString();
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null)
                {
                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) logger.LogInformation("[pnpm-dev] {Line}", e.Data);
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) logger.LogWarning("[pnpm-dev:err] {Line}", e.Data);
                    };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    logger.LogInformation(
                        "spawned pnpm dev:frontend in background at {RepoRoot} -> port 4427 (backend {BackendPort})",
                        repoRoot, backendPort);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "failed to spawn pnpm dev:frontend");
            }
        });
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromMilliseconds(200)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindRepoRootWithPnpm()
    {
        var walker = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        for (var depth = 0; depth < 10 && walker is not null; depth++)
        {
            if (File.Exists(Path.Combine(walker.FullName, "package.json"))
                && File.Exists(Path.Combine(walker.FullName, "pnpm-lock.yaml")))
            {
                return walker.FullName;
            }

            walker = walker.Parent;
        }

        return null;
    }

    private static void KillProcessTreeOnPort(int port, ILogger logger)
    {
        try
        {
            var pids = GetPidsListeningOnPort(port, logger);
            if (pids.Count == 0) return;

            logger.LogInformation(
                "port {Port} sudah dipakai sebelum spawn, membersihkan {Count} proses lama",
                port, pids.Count);

            foreach (var pid in pids)
            {
                try
                {
                    var p = System.Diagnostics.Process.GetProcessById(pid);
                    logger.LogInformation(
                        "killing pid={Pid} ({Name}) yang mendengarkan port {Port}",
                        pid, p.ProcessName, port);
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "gagal kill pid={Pid}", pid);
                }
            }

            System.Threading.Thread.Sleep(500);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "gagal membersihkan port {Port}", port);
        }
    }

    private static HashSet<int> GetPidsListeningOnPort(int port, ILogger logger)
    {
        var pids = new HashSet<int>();
        try
        {
            using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p TCP",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            ps.WaitForExit(2000);
            var output = ps.StandardOutput.ReadToEnd();
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("TCP", StringComparison.Ordinal)) continue;
                var parts = Regex.Split(trimmed, @"\s+");
                if (parts.Length < 5) continue;
                if (!parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                var local = parts[1];
                var localPort = local.Split(':').Last();
                if (!int.TryParse(localPort, out var lp) || lp != port) continue;
                if (int.TryParse(parts[4], out var pid)) pids.Add(pid);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "netstat gagal");
        }

        return pids;
    }
}

// ======================================================================
// TestAppBuilder — used by MemoryApiTests to create a test WebApplication
// from the same entry point (no WebApplicationFactory<Program> needed).
// ======================================================================
public static class TestAppBuilder
{
    public static WebApplication Create(
        AppServices shared,
        ProjectContext context,
        int dashboardPort,
        ILoggerFactory loggerFactory
    )
        => HttpLoop.BuildWebApplication(shared, context, dashboardPort, false, loggerFactory);

    /// <summary>
    /// Convenience: build a self-contained test host using a fresh
    /// <see cref="AppServices"/> and the project's discovered context.
    /// </summary>
    public static WebApplication CreateSelfContained(
        int dashboardPort = 0
    )
    {
        // Override the data dir to a per-process temp dir so tests don't
        // pollute the real .eling store.
        var tempDir = Path.Combine(Path.GetTempPath(), "eling-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, ".eling"));

        // Build minimal project/user scope pointing at the temp dir.
        var projectRoot = tempDir;
        var projectScope = new ProjectScope(projectRoot);
        var userScope = UserScope.Resolve(Environment.GetEnvironmentVariable("ELING_USER_SCOPE"));
        var context = new ProjectContext(
            projectScope,
            userScope,
            Path.Combine(projectRoot, ".eling"),
            IsUserHome: false);

        var shared = AppServices.Create(context);
        return Create(shared, context, dashboardPort, shared.LoggerFactory);
    }
}