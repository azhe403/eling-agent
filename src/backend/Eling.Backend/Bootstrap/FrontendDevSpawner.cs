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
    /// The loop this process is running, or null when none is. Identity, not a
    /// flag: <see cref="TrySpawn"/> is called from inside the HTTP host's own
    /// restart loop, so two callers can overlap with the previous loop's exit.
    /// A plain flag cannot say who owns it - the exiting loop would clear a flag
    /// the new loop already set (starting a third loop), or clear its own flag
    /// after the new caller had already been turned away (leaving none at all,
    /// which is the silent auto-spawn disable this guard exists to prevent).
    /// </summary>
    private static Task? _loop;

    private static readonly object LoopGate = new();

    /// <summary>
    /// State carried across rounds. A class rather than a struct because async
    /// methods cannot take ref parameters.
    /// </summary>
    private sealed class FrontendSession
    {
        public Task? Running { get; set; }

        /// <summary>
        /// The root process of the tree this backend spawned, as reported by
        /// CliWrap. Killing this PID with its whole tree is what actually
        /// releases port 4427: the socket belongs to a deep grandchild
        /// (<c>cmd -&gt; pnpm -&gt; cmd -&gt; pnpm -&gt; next dev -&gt; node</c>),
        /// and killing only the leaf leaves the wrappers alive holding the
        /// port.
        /// </summary>
        public int? RootProcessId { get; set; }

        /// <summary>When the current spawn was started, for the grace period.</summary>
        public DateTimeOffset? SpawnedAt { get; set; }

        public int ConsecutiveFailures { get; set; }

        /// <summary>Consecutive probe rounds where 4427 listened but never answered.</summary>
        public int ConsecutiveSilentProbes { get; set; }

        /// <summary>
        /// Whether the frontend has answered a probe at least once since this
        /// spawn. Once it has, it has come up: a later silence is a hang, not a
        /// slow first compile, and the startup grace period must not shield it.
        /// </summary>
        public bool RespondedSinceSpawn { get; set; }
    }

    public static void TrySpawn(ProjectContext context, int backendPort, ILogger logger, CancellationToken cancellationToken = default)
    {
        var repoRoot = DevModeDetector.FindRepoRootWithPnpm();
        if (repoRoot is null) return;

        // Declared and constructed before the lock so the lambda below captures
        // a non-null local: a nullable captured by a closure is a real way for
        // this to NRE, and the compiler cannot prove the assignment happened.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (LoopGate)
        {
            if (_loop is { IsCompleted: false })
            {
                logger.LogDebug("Frontend dev reconcile loop already running in this process; not starting another");
                return;
            }

            _loop = started.Task;
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
                lock (LoopGate)
                {
                    // Only the loop that owns the slot may release it. Whoever
                    // replaced us owns the slot now, and clearing it would hand
                    // the same slot to a third loop while theirs still runs.
                    if (ReferenceEquals(_loop, started.Task))
                        _loop = null;
                }

                started.TrySetResult();
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

        // [healthcheck] A frontend that has answered at least once has come up.
        // From then on, silence is a hang - not a slow first compile - so the
        // startup grace period stops applying and the healthcheck can act.
        if (responding)
        {
            session.RespondedSinceSpawn = true;
        }

        // [healthcheck] Track consecutive silent probes. A frontend that holds
        // 4427 but stops answering is hung — but a single silent probe can also
        // be a slow on-demand compile (>10s first hit). Only when the silent
        // streak reaches FrontendReconcile.SilentProbeThreshold do we kill.
        var silentProbe = listening && !responding && !starting;
        if (silentProbe)
        {
            session.ConsecutiveSilentProbes++;
        }
        else
        {
            session.ConsecutiveSilentProbes = 0;
        }

        var action = FrontendReconcile.Decide(
            listening, responding, starting, session.ConsecutiveSilentProbes);

        // [healthcheck] The streak is what separates "still compiling" from
        // "hung". Until it is proven, the frontend is left alone and re-probed
        // soon rather than killed.
        if (action == FrontendAction.None && listening && !responding)
        {
            logger.LogWarning(
                "[healthcheck] port {Port} silent ({Silent}/{Threshold}); re-probing before kill",
                FrontendPort, session.ConsecutiveSilentProbes, FrontendReconcile.SilentProbeThreshold);

            // A silent frontend is not a failed attempt, so the spawn backoff
            // must not grow because of it - the next probe either clears the
            // streak or proves the hang.
            session.ConsecutiveFailures = 0;
            return StartupPollInterval;
        }

        // [healthcheck] Once the streak proves a hang, announce the pending
        // kill+restart so `pnpm dev` recovery is visible in backend logs.
        if (action == FrontendAction.Restart)
        {
            logger.LogWarning(
                "[healthcheck] port {Port} silent ({Silent}/{Threshold}); killing and restarting pnpm dev",
                FrontendPort, session.ConsecutiveSilentProbes, FrontendReconcile.SilentProbeThreshold);
        }

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
            // Only a healthy round clears the backoff. A round left alone only
            // because our own spawn is still coming up is not evidence of
            // health, and resetting there would let a crashing spawn retry at
            // full speed forever. A round where the frontend answers - our own
            // spawn or not - is the healthy case, and it must reset: without
            // this, the counter climbed for the whole life of a correctly
            // working process and the next restart waited the 60s ceiling.
            if (listening && responding)
                session.ConsecutiveFailures = 0;
            else if (!childAlive)
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
    /// <remarks>
    /// The grace period also ends as soon as the frontend answers a probe. It
    /// exists to cover the first compile, and a frontend that has already
    /// served once is not compiling for the first time any more - so a later
    /// silence is a hang and the healthcheck has to be able to act on it.
    /// Without this, a frontend that served and then wedged would stay
    /// untouchable for the whole grace window.
    /// </remarks>
    private static bool IsStarting(FrontendSession session)
    {
        if (session.Running is not { IsCompleted: false } || session.SpawnedAt is not { } spawnedAt)
            return false;

        if (session.RespondedSinceSpawn)
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
        if (action == FrontendAction.Restart)
        {
            logger.LogWarning(
                "[healthcheck] port {Port} stayed silent for {Silent} probe(s); killing the hung frontend",
                FrontendPort, session.ConsecutiveSilentProbes);
        }

        // Kill our own spawn first, from the root. The socket belongs to a
        // deep grandchild of this tree, so killing the leaf leaves the pnpm
        // wrappers alive and the port still bound - which is how the respawn
        // turned into EADDRINUSE.
        KillOwnSpawn(session, logger);
        KillProcessTreeOnPort(FrontendPort, logger);

        // Wait for the port before spawning, on both paths. A spawn into a
        // bound port is a guaranteed EADDRINUSE, so "the port looked free a
        // moment ago" is not good enough - the holder has to have released it.
        var freed = await WaitForPortFreeAsync(logger, cancellationToken);
        if (!freed)
        {
            // The port is still bound after the kill. Spawning into it is a
            // guaranteed EADDRINUSE, so abort this round instead and let the
            // next round retry - with a fresh streak so the hang is re-proven
            // rather than assumed.
            logger.LogWarning(
                "[healthcheck] port {Port} still bound after the kill; aborting the respawn and retrying next round",
                FrontendPort);

            // Forget the spawn we just killed. Without this the session still
            // looks like "starting" for the rest of the grace period, and
            // every round in that window resets the silent streak - so the hang
            // would not be re-detected until the grace period expires.
            session.SpawnedAt = null;
            session.RootProcessId = null;
            session.Running = null;
            session.RespondedSinceSpawn = false;
            session.ConsecutiveSilentProbes = 0;
            session.ConsecutiveFailures = 0;
            return;
        }

        session.ConsecutiveSilentProbes = 0;

        // A cancelled round must not spawn: StartFrontend runs on
        // CancellationToken.None, so it would start a real pnpm while the
        // process is shutting down.
        cancellationToken.ThrowIfCancellationRequested();

        StartFrontend(session, repoRoot, backendPort, logger);
        session.ConsecutiveFailures++;
    }

    /// <summary>
    /// Blocks until 4427 is released, so the replacement <c>next dev</c> does
    /// not race the process that was just killed into EADDRINUSE.
    /// Returns true when the port is free and it is safe to spawn, false
    /// when the holder survived the kill — in which case the caller must
    /// abort the spawn and retry later instead of spawning into a busy port.
    /// </summary>
    /// <remarks>
    /// Cancelling does not mean "stop waiting, carry on": the caller spawns the
    /// replacement next, and that spawn runs on <see cref="CancellationToken.None"/>.
    /// Swallowing the cancellation here would start a real <c>pnpm</c> during
    /// shutdown, so it is rethrown and the round ends instead.
    /// </remarks>
    private static async Task<bool> WaitForPortFreeAsync(ILogger logger, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + PortFreeTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await DevModeDetector.IsPortListeningAsync(FrontendPort, cancellationToken: cancellationToken))
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        logger.LogWarning(
            "[healthcheck] port {Port} still bound {Timeout}s after the kill; will retry kill or abort spawn",
            FrontendPort, PortFreeTimeout.TotalSeconds);
        return false;
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
        session.RespondedSinceSpawn = false;

        // Capture the root PID of the tree we are about to create. CliWrap
        // reports it once the process starts, and it is the only handle that
        // can take the whole tree down - see KillOwnSpawn.
        var rootProcessId = 0;
        session.Running = CliWrap.Cli.Wrap("pnpm")
            .WithArguments("dev:frontend")
            .WithWorkingDirectory(repoRoot)
            .WithEnvironmentVariables(env => env.Set("ELING_BACKEND_PORT", backendPort.ToString()))
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToDelegate(line => logger.LogInformation("[pnpm-dev] {Line}", line)))
            .WithStandardErrorPipe(CliWrap.PipeTarget.ToDelegate(line => logger.LogWarning("[pnpm-dev:err] {Line}", line)))
            .ExecuteAsync(
                configureProcessStartInfo: _ => { },
                configureProcess: process => rootProcessId = process.Id)
            .Task;

        session.RootProcessId = rootProcessId > 0 ? rootProcessId : null;

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

    /// <summary>
    /// Kills the process tree this backend spawned, starting from its root.
    /// </summary>
    /// <remarks>
    /// This is the only kill that reliably releases port 4427. The listening
    /// socket belongs to a node process several levels down
    /// (<c>cmd -&gt; pnpm -&gt; cmd -&gt; pnpm -&gt; next dev -&gt; node</c>), so
    /// killing the process that netstat names as the port owner takes down the
    /// leaf and leaves every wrapper above it running - the port stays bound
    /// and the respawn dies with EADDRINUSE. Killing the root we started
    /// takes the whole chain with it.
    /// <para>
    /// The root PID comes from CliWrap itself rather than from netstat, because
    /// netstat reports PID 0 for the listening socket on this machine, which
    /// is why the old lookup found nothing to kill.
    /// </para>
    /// </remarks>
    private static void KillOwnSpawn(FrontendSession session, ILogger logger)
    {
        if (session.RootProcessId is not { } rootPid || rootPid <= 0) return;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(rootPid);
            logger.LogInformation(
                "[healthcheck] killing the frontend tree we spawned: pid={Pid} ({Name})",
                rootPid, process.ProcessName);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // The tree is already gone - nothing to kill.
            logger.LogDebug("[healthcheck] the frontend tree we spawned (pid={Pid}) is already gone", rootPid);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[healthcheck] failed to kill the frontend tree we spawned (pid={Pid})", rootPid);
        }
    }

    private static void KillProcessTreeOnPort(int port, ILogger logger)
    {
        try
        {
            var pids = GetPidsListeningOnPort(port, logger);
            if (pids.Count == 0)
            {
                logger.LogWarning(
                    "[healthcheck] netstat named no owner PID for port {Port}; the holder may have exited between the probe and the kill",
                    port);
                return;
            }

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
                if (parts.Length < 4) continue;
                var local = parts[1];
                var localPort = local.Split(':').Last();
                if (!int.TryParse(localPort, out var lp) || lp != port) continue;
                // PID is always last: LISTENING lines have 5 parts, but a bound
                // line without a state (blank state) has only 4 parts, and a
                // half-dead holder shows up as ESTABLISHED/CLOSE_WAIT/TIME_WAIT
                // on 4427. Matching only LISTENING missed exactly the stuck
                // process we most need to kill, so match any local endpoint.
                if (int.TryParse(parts[^1], out var pid) && pid > 0 && pid != Environment.ProcessId) pids.Add(pid);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "netstat failed");
        }

        return pids;
    }
}