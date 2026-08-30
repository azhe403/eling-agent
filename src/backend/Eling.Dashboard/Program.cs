using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using CliWrap;
using Eling.Application;
using Eling.Core;
using Eling.Dashboard;
using Eling.Dashboard.Converters;
using Eling.Dashboard.Endpoints;
using Microsoft.AspNetCore.Http.Json;

// eling-dashboard: user-scoped shared Dashboard Coordinator.
// Hosts the dashboard HTTP API + web UI on 127.0.0.1:4317 (loopback only),
// tracks registered eling runtimes, and shuts down when none remain.
// It never runs MCP, never owns a project .eling, and never binds beyond loopback.

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Named differently from the eling-dashboard executable so both can sit
    // side by side in the install directory (matters on case-sensitive FS).
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "eling-dashboard-ui")
});

builder.Configuration.Sources.Clear();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, ResolveDashboardPort()));

static int ResolveDashboardPort() =>
    int.TryParse(Environment.GetEnvironmentVariable("ELING_DASHBOARD_PORT"), out var port) && port > 0
        ? port
        : 4317;

// Diagnostics go to stderr only; stdout stays clean for potential protocol use.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<RuntimeRegistry>();
builder.Services.AddSingleton<MemoryChangeBroadcaster>();
builder.Services.AddSingleton<IMemoryChangeNotifier>(sp => sp.GetRequiredService<MemoryChangeBroadcaster>());
builder.Services.AddSingleton<IMemoryScopePolicy, MemoryScopePolicy>();
builder.Services.AddSingleton<IMemoryMerger, MemoryMerger>();
builder.Services.AddScoped<IMemoryService>(sp =>
    sp.GetRequiredService<RuntimeRegistry>().ResolveMemoryService());
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new MemoryIdJsonConverter());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// Static pipeline order matters: defaults/static BEFORE routing, and
// MapFallbackToFile LAST, otherwise deep links get captured by the fallback.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", pid = Environment.ProcessId }));
app.MapGet("/api/events/memories", async (HttpContext context, MemoryChangeBroadcaster broadcaster) =>
{
    var ct = context.RequestAborted;
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-transform";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    await context.Response.WriteAsync("data: connected\n\n", ct);
    await context.Response.Body.FlushAsync(ct);

    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
    var subscribeTask = Task.Run(async () =>
    {
        await foreach (var evt in broadcaster.SubscribeAsync(ct))
        {
            await context.Response.WriteAsync($"data: {evt}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
    }, ct);

    var pingTask = Task.Run(async () =>
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            await context.Response.WriteAsync(": ping\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
    }, ct);

    await Task.WhenAny(subscribeTask, pingTask);
});
app.MapCoordinatorEndpoints();
app.MapMemoryRoutes();
app.MapScopedMemoryRoutes();
app.MapFallbackToFile("index.html");

var registry = app.Services.GetRequiredService<RuntimeRegistry>();
registry.ShutdownCallback = () =>
{
    try
    {
        _ = app.StopAsync(TimeSpan.FromSeconds(5));
    }
    catch (Exception ex)
    {
        // The sweeper timer swallows callback exceptions; surface failures here.
        Console.Error.WriteLine($"[eling-dashboard] graceful shutdown failed: {ex.Message}");
    }
};

// In dev mode (port != 4317, e.g. 4417 in dev MCP / isolated dev),
// auto-spawn `pnpm dev` so the Next.js live dev server (port 4427) starts
// together with the backend. This gives us Hot Reload without manual orchestration.
var currentDashboardPort = ResolveDashboardPort();
var isDevDashboard = currentDashboardPort != 4317;

if (isDevDashboard && !IsDevFrontendAlreadyRunning())
{
    TryStartPnpmDev(currentDashboardPort);
}
else if (isDevDashboard)
{
    Console.Error.WriteLine("[eling-dashboard] pnpm dev already running on port 4427, skipping spawn.");
}

// The actual bind is authoritative. When several eling processes spawn a
// dashboard simultaneously, exactly one owns 127.0.0.1:4317; the losers hit
// AddressInUse here and exit cleanly without touching the winner.
try
{
    app.Run();
}
catch (Exception ex) when (IsAddressInUse(ex))
{
    Console.Error.WriteLine($"[eling-dashboard] 127.0.0.1:{currentDashboardPort} is already owned by another dashboard; exiting cleanly.");
}

return;

static bool IsAddressInUse(Exception ex)
{
    for (var current = ex; current is not null; current = current.InnerException)
    {
        if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
            return true;
    }

    return false;
}

static bool IsDevFrontendAlreadyRunning()
{
    try
    {
        using var client = new System.Net.Sockets.TcpClient();
        var task = client.ConnectAsync("127.0.0.1", 4427);
        return task.Wait(TimeSpan.FromMilliseconds(200)) && client.Connected;
    }
    catch
    {
        return false;
    }
}

static void TryStartPnpmDev(int backendPort)
{
    try
    {
        // Walk up from the binary location to find the project root that contains package.json + pnpm-lock.yaml
        var walker = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string? repoRoot = null;
        for (var depth = 0; depth < 10 && walker is not null; depth++)
        {
            if (File.Exists(Path.Combine(walker.FullName, "package.json")) &&
                File.Exists(Path.Combine(walker.FullName, "pnpm-lock.yaml")))
            {
                repoRoot = walker.FullName;
                break;
            }
            walker = walker.Parent;
        }

        if (repoRoot is null)
        {
            Console.Error.WriteLine("[eling-dashboard] repo root with pnpm-lock.yaml not found, skipping pnpm dev spawn.");
            return;
        }

        var cmd = Cli.Wrap("pnpm")
            .WithArguments(["dev:frontend"])
            .WithWorkingDirectory(repoRoot)
            .WithEnvironmentVariables(env => env.Set("ELING_BACKEND_PORT", backendPort.ToString()))
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line => Console.Error.WriteLine($"[pnpm-dev] {line}")))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Console.Error.WriteLine($"[pnpm-dev:err] {line}")));

        Console.Error.WriteLine($"[eling-dashboard] spawning pnpm dev via CliWrap at {repoRoot} pointing to backend port {backendPort}");
        _ = cmd.ExecuteAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[eling-dashboard] failed to spawn pnpm dev: {ex.Message}");
    }
}

// Expose Program type to integration tests via WebApplicationFactory<Program>
public partial class Program;
