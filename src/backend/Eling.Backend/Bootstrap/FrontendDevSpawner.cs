using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

internal static class FrontendDevSpawner
{
    public static void TrySpawn(ProjectContext context, int backendPort, ILogger logger)
    {
        var repoRoot = FindRepoRootWithPnpm();
        if (repoRoot is null) return;

        if (IsPortListening(4427))
        {
            KillProcessTreeOnPort(4427, logger);
        }

        Task.Run(async () =>
        {
            try
            {
                var isWindows = OperatingSystem.IsWindows();
                var targetExe = isWindows ? "cmd.exe" : "pnpm";
                var targetArgs = isWindows ? new[] { "/c", "pnpm", "dev:frontend" } : new[] { "dev:frontend" };

                logger.LogInformation(
                    "spawning pnpm dev:frontend via CliWrap at {RepoRoot} -> port 4427 (backend {BackendPort})",
                    repoRoot, backendPort);

                await CliWrap.Cli.Wrap(targetExe)
                    .WithArguments(targetArgs)
                    .WithWorkingDirectory(repoRoot)
                    .WithEnvironmentVariables(env => env.Set("ELING_BACKEND_PORT", backendPort.ToString()))
                    .WithStandardOutputPipe(CliWrap.PipeTarget.ToDelegate(line => logger.LogInformation("[pnpm-dev] {Line}", line)))
                    .WithStandardErrorPipe(CliWrap.PipeTarget.ToDelegate(line => logger.LogWarning("[pnpm-dev:err] {Line}", line)))
                    .ExecuteAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "failed to spawn pnpm dev:frontend via CliWrap");
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
