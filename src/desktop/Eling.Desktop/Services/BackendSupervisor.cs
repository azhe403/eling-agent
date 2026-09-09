using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Eling.Desktop.Services;

public sealed class BackendSupervisor(ILogger<BackendSupervisor> logger) : IDisposable
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private Process? _spawnedProcess;

    public int? ActivePort { get; private set; }
    public string? BaseUrl => ActivePort.HasValue ? $"http://127.0.0.1:{ActivePort.Value}" : null;

    public async Task<string> EnsureBackendRunningAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Ensuring backend is running...");

        // 1. Check if backend is already running on dev port 4417
        if (await IsHealthyAsync(4417, cancellationToken))
        {
            ActivePort = 4417;
            logger.LogInformation("Found backend on port {Port}", ActivePort);
            return BaseUrl!;
        }

        // 2. Check if backend is already running on staging/default port 4317
        if (await IsHealthyAsync(4317, cancellationToken))
        {
            ActivePort = 4317;
            logger.LogInformation("Found backend on port {Port}", ActivePort);
            return BaseUrl!;
        }

        // 3. Spawn backend process
        logger.LogInformation("No backend found, spawning...");
        await SpawnBackendAsync(cancellationToken);

        // 4. Poll until healthy
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && !cancellationToken.IsCancellationRequested)
        {
            if (await IsHealthyAsync(4417, cancellationToken))
            {
                ActivePort = 4417;
                logger.LogInformation("Backend started on port {Port} after {Elapsed}s", ActivePort, sw.ElapsedMilliseconds / 1000.0);
                return BaseUrl!;
            }
            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException("Backend failed to start or respond to health check within 20 seconds.");
    }

    private async Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"http://127.0.0.1:{port}/health";
            logger.LogDebug("Probing {Url}", url);
            var response = await HttpClient.GetAsync(url, cancellationToken);
            logger.LogDebug("Port {Port}: {Status}", port, response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Port {Port}: {ErrorType}", port, ex.GetType().Name);
            return false;
        }
    }

    private Task SpawnBackendAsync(CancellationToken cancellationToken)
    {
        var repoRoot = FindRepoRoot();
        logger.LogInformation("Repo root: {RepoRoot}", repoRoot);

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };
        psi.EnvironmentVariables["ELING_DASHBOARD_PORT"] = "4417";

        // Try direct exe in base directory (distribution / side-by-side)
        var localExe = Path.Combine(AppContext.BaseDirectory, "eling-backend.exe");
        if (File.Exists(localExe))
        {
            logger.LogInformation("Using backend exe: {Path}", localExe);
            psi.FileName = localExe;
        }
        else
        {
            // Check in .bin directory if available
            var binExe = Path.Combine(repoRoot, ".bin", "Debug", "net10.0", "eling-backend.exe");
            if (!File.Exists(binExe))
            {
                binExe = Path.Combine(repoRoot, ".bin", "Release", "net10.0", "eling-backend.exe");
            }

            if (File.Exists(binExe))
            {
                logger.LogInformation("Using backend exe: {Path}", binExe);
                psi.FileName = binExe;
            }
            else
            {
                // Fallback: dotnet run
                var backendCsProj = Path.Combine(repoRoot, "src", "backend", "Eling.Backend", "Eling.Backend.csproj");
                logger.LogInformation("Using dotnet run: {Project}", backendCsProj);
                psi.FileName = "dotnet";
                psi.Arguments = $"run --project \"{backendCsProj}\" -p:ElingSkipDashboard=true";
            }
        }

        _spawnedProcess = Process.Start(psi);
        if (_spawnedProcess != null)
        {
            logger.LogInformation("Spawned backend PID {Pid}", _spawnedProcess.Id);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillSpawnedProcess();
        }

        return Task.CompletedTask;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Eling.slnx")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private void KillSpawnedProcess()
    {
        try
        {
            if (_spawnedProcess != null && !_spawnedProcess.HasExited)
            {
                logger.LogInformation("Killing spawned backend PID {Pid}", _spawnedProcess.Id);
                _spawnedProcess.Kill(entireProcessTree: true);
                _spawnedProcess.Dispose();
                _spawnedProcess = null;
            }
        }
        catch
        {
            // Ignore during shutdown
        }
    }

    public void Dispose()
    {
        KillSpawnedProcess();
    }
}
