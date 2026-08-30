using System.Diagnostics;
using System.Net.Http.Json;
using CliWrap;
using Eling.Application;
using Eling.Core;
using Eling.Host;
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

var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var isUserHome = !string.IsNullOrWhiteSpace(userHome) &&
    string.Equals(
        projectScope.Root.TrimEnd(Path.DirectorySeparatorChar),
        userHome.TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

// If host is spawned directly at user home without an active project repository,
// strictly use ~/.config/eling (global data directory) and never pollute user home with ~/.eling.
var effectiveDataDir = isUserHome ? userScope.GlobalDataDirectory : projectScope.DataDirectory;

Directory.CreateDirectory(effectiveDataDir);
Directory.CreateDirectory(userScope.RuntimeDirectory);

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddElingLogging(effectiveDataDir);
builder.Services.AddSingleton<IMemoryChangeNotifier>(new HttpCoordinatorMemoryChangeNotifier(DashboardPort));
builder.Services.AddElingCoreServices(effectiveDataDir);
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
    var exeName = OperatingSystem.IsWindows() ? "eling-dashboard.exe" : "eling-dashboard";
    var ownDirectory = Path.GetDirectoryName(Environment.ProcessPath);
    var pairedExe = ownDirectory is null ? null : Path.Combine(ownDirectory, exeName);
    var siblingDll = Path.Combine(AppContext.BaseDirectory, "eling-dashboard.dll");

    // In dev mode (e.g. port 4417 or source repository), check if Eling.Dashboard.csproj exists.
    // If so, launch `dotnet watch --project ...` so backend dashboard also hot-reloads on C# changes!
    string? dashboardCsproj = null;
    string? repoDll = null;
    string? repoRoot = null;
    var walker = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    for (var depth = 0; depth < 10 && walker is not null; depth++)
    {
        var candidate = Path.Combine(walker.FullName, "src", "backend", "Eling.Dashboard", "Eling.Dashboard.csproj");
        if (File.Exists(candidate))
        {
            dashboardCsproj = candidate;
            repoRoot = walker.FullName;
            break;
        }

        walker = walker.Parent;
    }

    Command cmd;

    if (dashboardCsproj is not null && repoRoot is not null)
    {
        cmd = Cli.Wrap("dotnet")
            .WithArguments(["run", "--project", dashboardCsproj, "--no-build"])
            .WithWorkingDirectory(repoRoot);
    }
    else if (pairedExe is not null && File.Exists(pairedExe))
    {
        cmd = Cli.Wrap(pairedExe);
    }
    else if (File.Exists(siblingDll))
    {
        cmd = Cli.Wrap("dotnet")
            .WithArguments(["exec", siblingDll]);
    }
    else if (repoDll is not null)
    {
        cmd = Cli.Wrap("dotnet")
            .WithArguments(["exec", repoDll]);
    }
    else
    {
        Console.Error.WriteLine("[eling] eling-dashboard binary not found; continuing without dashboard.");
        return;
    }

    // Configure environment and safely redirect all child output to STDERR
    // to preserve MCP stdout JSON-RPC protocol purity.
    cmd = cmd
        .WithEnvironmentVariables(env => env.Set("ELING_DASHBOARD_PORT", DashboardPort.ToString()))
        .WithStandardOutputPipe(PipeTarget.ToDelegate(line => Console.Error.WriteLine($"[dashboard-watch] {line}")))
        .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Console.Error.WriteLine($"[eling-dashboard] {line}")));

    cmd = cmd
        .WithEnvironmentVariables(env => env.Set("ELING_DASHBOARD_PORT", DashboardPort.ToString()))
        .WithStandardOutputPipe(PipeTarget.ToDelegate(line => Console.Error.WriteLine($"[dashboard-watch] {line}")))
        .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Console.Error.WriteLine($"[eling-dashboard] {line}")));

    try
    {
        var task = cmd.ExecuteAsync();
        _ = task.Task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Console.Error.WriteLine($"[eling] dashboard process faulted: {t.Exception?.GetBaseException().Message}");
            }
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[eling] failed to spawn eling-dashboard via CliWrap: {ex.Message}");
    }
}

async Task RegisterAsync(CancellationToken stopping)
{
    var registration = new RuntimeRegistration
    {
        ProcessId = Environment.ProcessId,
        // Distinguish global-only host runs from real project runs: use a unique
        // sentinel projectRoot so the runtime registry does not aggregate the same
        // ~/.config/eling storage twice (once as "project", once as "global").
        ProjectRoot = isUserHome ? "UserScope" : projectScope.Root,
        DataDirectory = effectiveDataDir,
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
