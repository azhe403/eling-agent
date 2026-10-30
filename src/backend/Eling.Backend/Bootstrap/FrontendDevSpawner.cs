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
///
/// The round is also blind to the difference between "not answering yet" and
/// "hung", which is why <see cref="StartupGracePeriod"/> exists: the probe only
/// describes a frontend that has had time to come up.
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
    /// Budget for a single probe. The probe accepts any answer, including the
    /// <c>307</c> that <c>/</c> returns, so a live server responds in
    /// milliseconds and this window is only ever spent on a server that has
    /// stopped reading requests at all.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a spawn of our own is left alone before its silence counts as a
    /// hang. <c>pnpm</c> has to start node, Next.js has to bind 4427, and the
    /// first request may still be compiling, so a round that lands inside this
    /// window decides nothing: judging that startup with the probe alone made
    /// the loop kill a frontend that was three seconds from serving, respawn
    /// it, and repeat forever.
    /// </summary>
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long to wait for a killed process to release 4427 before spawning.
    /// Spawning into a port the previous generation still holds is what made
    /// <c>pnpm</c> exit 255 about a second after every restart, with no output.
    /// </summary>
    private static readonly TimeSpan PortFreeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Redirects are deliberately not followed. <c>/</c> answers 307 towards
    /// <c>/dashboard</c>, and following it made every probe compile the
    /// heaviest route: a compile slower than the timeout read as "hung", the
    /// loop killed a healthy server, and the respawn raced the still-living
    /// <c>next dev</c> child into EADDRINUSE.
    /// </summary>
    private static readonly HttpClient HttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = ProbeTimeout,
    };

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

        /// <summary>When the current spawn was started, for the grace period.</summary>
        public DateTimeOffset? SpawnedAt { get; set; }

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

        // CancellationToken.None for the scheduling call: a cancelled token
        // would skip the delegate entirely and leave the guard above set with
        // no loop behind it. The loop observes the real token itself.
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileLoopAsync(repoRoot, backendPort, logger, cancellationToken);
            }
            finally
            {
                // Release the guard when the loop exits. Without this a loop
                // that ended for any reason switched auto-spawn off for the
                // rest of the process's life: every later TrySpawn only logged
                // "already running" and returned.
                Interlocked.Exchange(ref _loopStarted, 0);
            }
        }, CancellationToken.None);
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
        var starting = IsStarting(session);
        var action = FrontendReconcile.Decide(listening, responding, starting);

        // Our own child is still running: never spawn a competitor, whatever
        // the decision was. The grace period bounds how long such a child is
        // spared the kill, but this bound is not a timeout - it lasts exactly
        // as long as the child does. Without it a slow compile that outlived
        // the grace period got a second pnpm spawned next to it, which is the
        // EADDRINUSE race this whole loop exists to avoid.
        var childAlive = session.Running is { IsCompleted: false };
        if (childAlive && action == FrontendAction.Start)
            action = FrontendAction.None;

        if (action == FrontendAction.None)
        {
            // Only a healthy round clears the backoff. A round that was left
            // alone because our own spawn is still alive is not evidence of
            // health, and resetting here would let a crashing spawn retry at
            // full speed forever.
            if (!childAlive)
                session.ConsecutiveFailures = 0;
        }
        else
        {
            await ApplyActionAsync(session, action, repoRoot, backendPort, logger, cancellationToken);
        }

        return NextDelay(session, action, starting);
    }

    /// <summary>
    /// True while a spawn of ours is running and still inside
    /// <see cref="StartupGracePeriod"/>. A spawn whose process already exited is
    /// not "starting" - that is a failed attempt, and the loop should retry it
    /// (with backoff) rather than wait out the grace period.
    /// </summary>
    private static bool IsStarting(FrontendSession session)
    {
        if (session.Running is not { IsCompleted: false } || session.SpawnedAt is not { } spawnedAt)
            return false;

        return DateTimeOffset.UtcNow - spawnedAt < StartupGracePeriod;
    }

    private static async Task ApplyActionAsync(
        FrontendSession session,
        FrontendAction action,
        string repoRoot,
        int backendPort,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // KillProcessTreeOnPort is a no-op when nothing holds the port, so
        // Start and Restart share one path.
        if (action == FrontendAction.Restart)
        {
            logger.LogWarning(
                "port {Port} stayed silent for {Grace}s; treating the frontend as hung and terminating it",
                FrontendPort, StartupGracePeriod.TotalSeconds);
            KillProcessTreeOnPort(FrontendPort, logger);
            await WaitForPortFreeAsync(logger, cancellationToken);
        }

        StartFrontend(session, repoRoot, backendPort, logger);
        session.ConsecutiveFailures++;
    }

    /// <summary>
    /// Blocks until 4427 is released, so the replacement <c>next dev</c> does
    /// not race the process that was just killed into EADDRINUSE.
    /// </summary>
    private static async Task WaitForPortFreeAsync(ILogger logger, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + PortFreeTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await DevModeDetector.IsPortListeningAsync(FrontendPort, cancellationToken: cancellationToken))
                return;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        logger.LogWarning(
            "port {Port} still bound {Timeout}s after the kill; spawning anyway",
            FrontendPort, PortFreeTimeout.TotalSeconds);
    }

    private static TimeSpan NextDelay(FrontendSession session, FrontendAction action, bool starting)
    {
        // Poll fast while our own spawn comes up, whatever the decision was.
        if (starting)
            return StartupPollInterval;

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

        session.SpawnedAt = DateTimeOffset.UtcNow;
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
    /// <remarks>
    /// Any answer counts, not just a success status: <c>/</c> is a 307 and a
    /// route with a compile error answers 500, and both prove the server is
    /// alive and serving. Restarting on those would kill a working server and
    /// recompile the same error forever.
    /// </remarks>
    private static async Task<bool> ProbeRespondsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync($"http://127.0.0.1:{FrontendPort}/", cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Probe failure is expected control flow (frontend down/starting); caller treats false as "not responding".
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
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            // Read before waiting: a netstat that outlives the timeout would
            // otherwise block ReadToEnd forever, since the pipe stays open for
            // as long as the child lives.
            var pending = ps.StandardOutput.ReadToEndAsync();
            if (!ps.WaitForExit(2000))
            {
                try { ps.Kill(entireProcessTree: true); } catch { /* already gone */ }
                logger.LogWarning("netstat did not exit within 2s; port owners unknown this round");
                return pids;
            }

            var output = pending.GetAwaiter().GetResult();
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