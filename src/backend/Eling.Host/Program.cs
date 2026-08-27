using System.Diagnostics;
using System.Net.Http.Json;
using Eling.Core;
using Eling.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// eling: project-scoped MCP runtime over stdio.
//
// The shared dashboard (eling-dashboard) is an auxiliary control plane:
// this process ensures one exists, registers itself, keeps it alive via
// heartbeat, and unregisters on exit. Every dashboard failure is written
// to STDERR and ignored — MCP keeps running. Stdout carries MCP protocol
// traffic ONLY.

var DashboardPort = ResolveDashboardPort();
var noDashboard = args.Contains("--no-dashboard");

static int ResolveDashboardPort() =>
    int.TryParse(Environment.GetEnvironmentVariable("ELING_DASHBOARD_PORT"), out var port) && port > 0
        ? port
        : 4317;

var projectScope = ProjectScope.Discover();
var userScope = UserScope.Resolve();
Directory.CreateDirectory(projectScope.DataDirectory);
Directory.CreateDirectory(userScope.RuntimeDirectory);

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddElingLogging(projectScope.DataDirectory);
builder.Services.AddElingCoreServices(projectScope.DataDirectory);
builder.Services.AddElingMcpServerStdio();

var host = builder.Build();

if (!noDashboard)
{
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    _ = Task.Run(() => DashboardLoopAsync(lifetime.ApplicationStopping));

    // Best-effort graceful deregistration; crash paths are covered by the
    // dashboard's stale/grace sweeper instead.
    lifetime.ApplicationStopping.Register(() =>
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            client
                .DeleteAsync($"http://127.0.0.1:{DashboardPort}/api/coordinator/unregister/{Environment.ProcessId}")
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Dashboard may already be gone; nothing to do.
        }
    });
}

host.Run();
return;

async Task DashboardLoopAsync(CancellationToken stopping)
{
    while (!stopping.IsCancellationRequested)
    {
        try
        {
            // One code path for startup AND recovery: if the heartbeat fails
            // (dashboard missing/restarted), re-run ensure+register.
            if (!await TryHeartbeatAsync(stopping))
            {
                await EnsureDashboardAsync(stopping);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[eling] dashboard coordination failed: {ex.Message}");
        }

        try
        {
            await Task.Delay(HeartbeatInterval(), stopping);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

async Task EnsureDashboardAsync(CancellationToken stopping)
{
    if (await IsHealthyAsync(stopping))
    {
        await RegisterAsync(stopping);
        return;
    }

    SpawnDashboard();

    if (!await WaitForHealthyAsync(stopping))
    {
        Console.Error.WriteLine("[eling] dashboard did not become healthy; continuing without it.");
        return;
    }

    await RegisterAsync(stopping);
}

void SpawnDashboard()
{
    var psi = new ProcessStartInfo
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    var exeName = OperatingSystem.IsWindows() ? "eling-dashboard.exe" : "eling-dashboard";
    var ownDirectory = Path.GetDirectoryName(Environment.ProcessPath);
    var pairedExe = ownDirectory is null ? null : Path.Combine(ownDirectory, exeName);
    var siblingDll = Path.Combine(AppContext.BaseDirectory, "eling-dashboard.dll");

    // Dev layout: the paired eling + eling-dashboard binaries are built together
    // beside each other in .bin/ (for `dotnet run`/`watch`), while test/CI builds
    // use .artifacts/bin/ (via --artifacts-path). Prefer the sibling next to the
    // host; fall back to walking up to a .artifacts tree when running from a
    // standalone/published binary.
    string? repoArtifactsDll = null;
    var walker = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    for (var depth = 0; depth < 10 && walker is not null; depth++)
    {
        var candidateRoot = Path.Combine(walker.FullName, ".artifacts", "bin", "Eling.Dashboard");
        foreach (var configuration in Directory.Exists(candidateRoot)
                     ? Directory.GetDirectories(candidateRoot)
                     : [])
        {
            var candidate = Path.Combine(configuration, "eling-dashboard.dll");
            if (File.Exists(candidate)) { repoArtifactsDll = candidate; break; }
        }

        if (repoArtifactsDll is not null) break;
        walker = walker.Parent;
    }

    if (pairedExe is not null && File.Exists(pairedExe))
    {
        psi.FileName = pairedExe;
    }
    else if (File.Exists(siblingDll))
    {
        psi.FileName = "dotnet";
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(siblingDll);
    }
    else if (repoArtifactsDll is not null)
    {
        psi.FileName = "dotnet";
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(repoArtifactsDll);
    }
    else
    {
        Console.Error.WriteLine("[eling] eling-dashboard binary not found; continuing without dashboard.");
        return;
    }

    var process = Process.Start(psi);
    if (process is null)
    {
        Console.Error.WriteLine("[eling] failed to spawn eling-dashboard.");
        return;
    }

    // Drain pipes so the child never blocks on a full buffer; forward its
    // stderr for diagnostics, discard stdout.
    _ = process.StandardOutput.ReadToEndAsync();
    _ = ForwardStderrAsync(process);
}

static async Task ForwardStderrAsync(Process process)
{
    while (await process.StandardError.ReadLineAsync() is { } line)
    {
        Console.Error.WriteLine($"[eling-dashboard] {line}");
    }
}

async Task RegisterAsync(CancellationToken stopping)
{
    var registration = new RuntimeRegistration
    {
        ProcessId = Environment.ProcessId,
        ProjectRoot = projectScope.Root,
        DataDirectory = projectScope.DataDirectory,
        StartTime = GetStartTime(),
        McpEnabled = true,
        McpTransport = "stdio"
    };

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    using var response = await client.PostAsJsonAsync(
        $"http://127.0.0.1:{DashboardPort}/api/coordinator/register",
        registration,
        CoordinatorJsonContext.Default.RuntimeRegistration,
        stopping);
    response.EnsureSuccessStatusCode();
}

async Task<bool> TryHeartbeatAsync(CancellationToken stopping)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{DashboardPort}/api/coordinator/heartbeat/{Environment.ProcessId}",
            content: null,
            stopping);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

async Task<bool> IsHealthyAsync(CancellationToken stopping)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var response = await client.GetAsync($"http://127.0.0.1:{DashboardPort}/health", stopping);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

async Task<bool> WaitForHealthyAsync(CancellationToken stopping)
{
    // Bounded wait for the spawned dashboard to win the bind race and serve.
    for (var attempt = 0; attempt < 40 && !stopping.IsCancellationRequested; attempt++)
    {
        if (await IsHealthyAsync(stopping)) return true;
        await Task.Delay(250, stopping);
    }

    return false;
}

static DateTimeOffset GetStartTime()
{
    try
    {
        return new DateTimeOffset(Process.GetCurrentProcess().StartTime);
    }
    catch
    {
        return DateTimeOffset.Now;
    }
}

static TimeSpan HeartbeatInterval() =>
    int.TryParse(Environment.GetEnvironmentVariable("ELING_TEST_HEARTBEAT_MS"), out var ms) && ms > 0
        ? TimeSpan.FromMilliseconds(ms)
        : TimeSpan.FromSeconds(15);
