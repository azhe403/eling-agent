using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Keeps port 4427 serving the dashboard dev server.
/// </summary>
/// <remarks>
/// The loop reconciles rather than reacting to exits: every round probes 4427
/// and decides whether to spawn, restart, or leave it alone. The previous
/// version restarted only when the <c>pnpm</c> process returned, so a frontend
/// that was still alive but wedged was never touched. And because
/// <c>dotnet watch</c> restarts the backend without taking <c>pnpm dev</c>
/// down with it, the next generation saw 4427 still bound, decided there was
/// nothing to manage, and stopped supervising entirely.
/// </remarks>
internal static class FrontendDevSpawner
{
    private const int FrontendPort = 4427;

    /// <summary>
    /// Poll interval while a freshly spawned frontend is still starting. Next.js
    /// builds routes on demand, so probing too early catches a process that is
    /// still compiling and wrongly concludes it is hung.
    /// </summary>
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Poll interval while the frontend is healthy.</summary>
    private static readonly TimeSpan HealthyPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Budget for a single probe. Deliberately long: a frontend that is
    /// compiling still answers within this window, while one that is genuinely
    /// wedged never answers at all.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(50);

    private static readonly HttpClient HttpClient = new() { Timeout = ProbeTimeout };

    /// <summary>
    /// One loop per process. TrySpawn is called from inside the HTTP host's own
    /// restart loop, so without this guard each restart would start another
    /// reconcile loop and they would contend for port 4427.
    /// </summary>
    private static int _loopStarted;

    /// <summary>
    /// State carried across rounds. A class rather than a struct because async
    /// methods cannot take ref parameters.
    /// </summary>
    private sealed class FrontendSession
    {
        public Task? Running { get; set; }

        public int ConsecutiveFailures { get; set; }
    }

    public static void TrySpawn(ProjectContext context, int backendPort, ILogger logger, CancellationToken cancellationToken = default)
    {
        var repoRoot = DevModeDetector.FindRepoRootWithPnpm();
        if (repoRoot is null) return;

        if (Interlocked.Exchange(ref _loopStarted, 1) == 1)
        {
            logger.LogDebug("Frontend dev reconcile loop already running in this process; not starting another");
            return;
        }

        Task.Run(() => ReconcileLoopAsync(repoRoot, backendPort, logger, cancellationToken), cancellationToken);
    }

    private static async Task ReconcileLoopAsync(string repoRoot, int backendPort, ILogger logger, CancellationToken cancellationToken)
    {
        var session = new FrontendSession();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delay = await ReconcileOnceAsync(session, repoRoot, backendPort, logger, cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Frontend dev reconcile loop stopped cleanly via cancellation");
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Frontend dev reconcile loop error; continuing to the next round");
                try
                {
                    await Task.Delay(FrontendReconcile.RestartDelayAfter(++session.ConsecutiveFailures), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static async Task<TimeSpan> ReconcileOnceAsync(
        FrontendSession session,
        string repoRoot,
        int backendPort,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var listening = await DevModeDetector.IsPortListeningAsync(FrontendPort, cancellationToken: cancellationToken);
        var responding = listening && await ProbeRespondsAsync(cancellationToken);
        var action = FrontendReconcile.Decide(listening, responding);

        if (action == FrontendAction.None)
            session.ConsecutiveFailures = 0;
        else
            ApplyAction(session, action, repoRoot, backendPort, logger);

        return NextDelay(session, action);
    }

    private static void ApplyAction(
        FrontendSession session,
        FrontendAction action,
        string repoRoot,
        int backendPort,
        ILogger logger)
    {
        // KillProcessTreeOnPort is a no-op when nothing holds the port, so
        // Start and Restart share one path.
        if (action == FrontendAction.Restart)
        {
            logger.LogWarning(
                "port {Port} did not respond within {Timeout}s; treating the frontend as hung and terminating it",
                FrontendPort, ProbeTimeout.TotalSeconds);
            KillProcessTreeOnPort(FrontendPort, logger);
        }

        StartFrontend(session, repoRoot, backendPort, logger);
        session.ConsecutiveFailures++;
    }

    private static TimeSpan NextDelay(FrontendSession session, FrontendAction action)
    {
        if (action == FrontendAction.None)
            return HealthyPollInterval;

        // Still running but not finished starting: wait, do not spawn again.
        if (session.Running is { IsCompleted: false })
            return StartupPollInterval;

        return FrontendReconcile.WithJitter(
            FrontendReconcile.RestartDelayAfter(session.ConsecutiveFailures));
    }

    private static void StartFrontend(FrontendSession session, string repoRoot, int backendPort, ILogger logger)
    {
        logger.LogInformation(
            "spawning pnpm dev:frontend via CliWrap at {RepoRoot} -> port {Port} (backend {BackendPort})",
            repoRoot, FrontendPort, backendPort);

        session.Running = CliWrap.Cli.Wrap("pnpm")
            .WithArguments("dev:frontend")
            .WithWorkingDirectory(repoRoot)
            .WithEnvironmentVariables(env => env.Set("ELING_BACKEND_PORT", backendPort.ToString()))
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToDelegate(line => logger.LogInformation("[pnpm-dev] {Line}", line)))
            .WithStandardErrorPipe(CliWrap.PipeTarget.ToDelegate(line => logger.LogWarning("[pnpm-dev:err] {Line}", line)))
            .ExecuteAsync(CancellationToken.None);

        // The spawn task is deliberately not awaited - the loop has to stay free
        // to probe - so its failure has to be observed here or the exception is
        // lost.
        _ = session.Running.ContinueWith(
            task => logger.LogWarning(
                "pnpm dev:frontend exited ({Status}); the reconcile loop will handle it",
                task.Exception?.GetBaseException().Message ?? task.Status.ToString()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Requests <c>/</c> from the frontend dev server. Deliberately not an
    /// <c>/api/*</c> path: next.config.ts rewrites <c>/api/:path*</c> to the
    /// backend, so probing there would measure the backend's health and make
    /// this loop restart the frontend indefinitely whenever the backend is the
    /// thing that is unwell.
    /// </summary>
    private static async Task<bool> ProbeRespondsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync($"http://127.0.0.1:{FrontendPort}/", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void KillProcessTreeOnPort(int port, ILogger logger)
    {
        try
        {
            var pids = GetPidsListeningOnPort(port, logger);
            if (pids.Count == 0) return;

            logger.LogInformation(
                "port {Port} was already taken before spawn; clearing {Count} stale processes",
                port, pids.Count);

            foreach (var pid in pids)
            {
                try
                {
                    var p = System.Diagnostics.Process.GetProcessById(pid);
                    logger.LogInformation(
                        "killing pid={Pid} ({Name}) listening on port {Port}",
                        pid, p.ProcessName, port);
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "failed to kill pid={Pid}", pid);
                }
            }

            System.Threading.Thread.Sleep(500);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to clear port {Port}", port);
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
            logger.LogWarning(ex, "netstat failed");
        }

        return pids;
    }
}