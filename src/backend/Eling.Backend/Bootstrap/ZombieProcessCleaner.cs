using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

internal static class ZombieProcessCleaner
{
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(1) };

    public static async Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"http://127.0.0.1:{port}/health";
            using var response = await HealthClient.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> TryClearZombieOnPortAsync(
        int port,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // 1. If the port is healthy, it's not a zombie — it's a live owner.
        if (await IsHealthyAsync(port, cancellationToken))
        {
            logger.LogInformation("Port {Port} is actively serving healthy dashboard responses; will run as peer", port);
            return false;
        }

        // 2. Query PIDs holding the port
        var pids = await GetPidsOnPortAsync(port, logger, cancellationToken);
        var currentPid = Environment.ProcessId;
        var killedAny = false;

        foreach (var pid in pids)
        {
            if (pid == currentPid || pid <= 0) continue;

            try
            {
                using var proc = Process.GetProcessById(pid);
                var procName = proc.ProcessName;
                if (procName.Equals("eling-backend", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime? startTime = null;
                    try { startTime = proc.StartTime; } catch { }

                    logger.LogWarning(
                        "[ZombieRecovery] Detected unresponsive zombie eling-backend process (PID {Pid}, started {StartTime:yyyy-MM-dd HH:mm:ss}) holding port {Port}. Initiating process termination...",
                        pid, startTime, port);

                    proc.Kill(entireProcessTree: true);
                    killedAny = true;

                    logger.LogInformation(
                        "[ZombieRecovery] Successfully killed zombie eling-backend process (PID {Pid}) holding port {Port}",
                        pid, port);
                }
                else
                {
                    logger.LogWarning(
                        "[ZombieRecovery] Port {Port} is in use by non-eling process '{ProcName}' (PID {Pid}); skipping kill to prevent unintended disruption",
                        port, procName, pid);
                }
            }
            catch (ArgumentException)
            {
                // Process already exited before we could kill it
                logger.LogInformation(
                    "[ZombieRecovery] Process (PID {Pid}) on port {Port} already exited",
                    pid, port);
                killedAny = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[ZombieRecovery] Failed to inspect or terminate PID {Pid} on port {Port}",
                    pid, port);
            }
        }

        if (killedAny)
        {
            // Give OS time to reclaim socket handles
            await Task.Delay(500, cancellationToken);
            return true;
        }

        return false;
    }

    private static async Task<HashSet<int>> GetPidsOnPortAsync(int port, ILogger logger, CancellationToken cancellationToken)
    {
        var pids = new HashSet<int>();
        if (!OperatingSystem.IsWindows())
        {
            return pids;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            var result = await Cli.Wrap("netstat")
                .WithArguments(["-ano", "-p", "TCP"])
                .ExecuteBufferedAsync(timeout.Token);

            var portSuffix = $":{port}";
            foreach (var line in result.StandardOutput.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("TCP", StringComparison.Ordinal)) continue;
                var parts = Regex.Split(trimmed, @"\s+");
                if (parts.Length < 4) continue;

                // parts[1] is local address e.g. 127.0.0.1:4317 or [::]:4317
                var local = parts[1];
                if (!local.EndsWith(portSuffix, StringComparison.Ordinal)) continue;

                var pidStr = parts[^1];
                if (int.TryParse(pidStr, out var pid))
                {
                    pids.Add(pid);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query netstat for port {Port}", port);
        }

        return pids;
    }
}
