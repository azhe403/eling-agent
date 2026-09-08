using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

internal static class FrontendDevSpawner
{
    public static void TrySpawn(ProjectContext context, int backendPort, ILogger logger, CancellationToken cancellationToken = default)
    {
        var repoRoot = DevModeDetector.FindRepoRootWithPnpm();
        if (repoRoot is null) return;

        Task.Run(async () =>
        {
            var restartDelay = TimeSpan.FromSeconds(2);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (await DevModeDetector.IsPortListeningAsync(4427, cancellationToken: cancellationToken))
                    {
                        KillProcessTreeOnPort(4427, logger);
                    }

                    logger.LogInformation(
                        "spawning pnpm dev:frontend via CliWrap at {RepoRoot} -> port 4427 (backend {BackendPort})",
                        repoRoot, backendPort);

                    await CliWrap.Cli.Wrap("pnpm")
                        .WithArguments("dev:frontend")
                        .WithWorkingDirectory(repoRoot)
                        .WithEnvironmentVariables(env => env.Set("ELING_BACKEND_PORT", backendPort.ToString()))
                        .WithStandardOutputPipe(CliWrap.PipeTarget.ToDelegate(line => logger.LogInformation("[pnpm-dev] {Line}", line)))
                        .WithStandardErrorPipe(CliWrap.PipeTarget.ToDelegate(line => logger.LogWarning("[pnpm-dev:err] {Line}", line)))
                        .ExecuteAsync(cancellationToken);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning("pnpm dev:frontend exited unexpectedly; restarting watchdog in {DelaySec}s...", restartDelay.TotalSeconds);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInformation("Frontend dev watchdog stopped cleanly via cancellation");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Frontend dev watchdog encountered error; restarting in {DelaySec}s...", restartDelay.TotalSeconds);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(restartDelay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, cancellationToken);
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
