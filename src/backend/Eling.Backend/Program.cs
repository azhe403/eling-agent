using Eling.Backend;
using Eling.Backend.Bootstrap;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// ======================================================================
// Top-level statements — entry point of eling-backend
// ======================================================================
//
// Dual-host architecture:
//   1. MCP Host (GenericHost) — lives for the full process lifetime, owns the
//      stdio JSON-RPC transport and all shared in-memory services (RuntimeRegistry,
//      MemoryChangeBroadcaster, ILoggerFactory).
//   2. HTTP Host — a WebApplication that tries to bind Kestrel to
//      127.0.0.1:dashboardPort. If the port is free it becomes owner and serves
//      REST + UI + spawns the frontend. If the port is taken it retries with
//      randomized jitter and stays MCP-only while the peer owns the port.
//   3. Frontend spawn — only the instance that owns the port spawns
//      `pnpm dev:frontend` on 4427; peers stay MCP-only.
//
// The loop guarantees that at most ONE process in the system becomes the
// "owner" that binds the port; all others stay MCP-only and provide the
// underlying data layer for the owner's REST clients.
//
// ======================================================================
// The opened folder = the working directory the host spawned us in.
// ======================================================================
var cwd = Environment.CurrentDirectory;
var dashboardPort = DashboardPort.Resolve();
var context = ProjectContext.Discover();

// 1. Build the shared service container (once, shared across both hosts).
using var shared = AppServices.Create(context);

// 2. Build the MCP host (GenericHost) — owns stdio transport + shared services.
var mcpHost = McpHostBuilder.Build(shared, context);

// CRITICAL: the GenericHost must be Started, not just Built. Hosted services
// (including the MCP stdio transport registered via AddElingMcpServerStdio)
// only activate on StartAsync. Without this, peer-mode processes (those that
// lose the dashboard-port race and stay MCP-only) have no active stdio server
// and opencode times out the JSON-RPC handshake, killing the process. Owners
// (which also start a WebApplication with its own MCP) were unaffected because
// the WebApplication's hosted services started via app.RunAsync().
await mcpHost.StartAsync();

// Self-register in RuntimeRegistry (both owner and peer processes write their
// runtime descriptor to disk and maintain heartbeats so the coordinator dashboard
// can discover all open projects).
RuntimeSelfRegistration.Wire(mcpHost, RuntimeSelfRegistration.Build(context));

// Log the opened folder and the scope Eling actually bound to. The static
// Serilog Log is used because AddElingLogging (run during the MCP host build
// above) binds the global Log.Logger eagerly, so the line lands in both stderr
// and the rolling daily file sink under <dataDir>/logs/mcp.log.
Log.Information(
    "Eling started - Opened folder: {Cwd}; Project root: {ProjectRoot}; Effective data dir: {DataDir}; User-home session: {IsUserHome}",
    cwd,
    context.ProjectScope.Root,
    context.EffectiveDataDir,
    context.IsUserHome);

// 3. Run the HTTP port-acquisition loop. Blocks until the process shuts down
//    (or the cancellation token fires from ApplicationStopping).
var httpResult = await HttpLoop.RunAsync(
    shared: shared,
    context: context,
    dashboardPort: dashboardPort,
    isDevMode: IsDevMode(),
    loggerFactory: mcpHost.Services.GetRequiredService<ILoggerFactory>(),
    cancellationToken: mcpHost.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

// 4. Graceful shutdown: stop the MCP host (releases stdio + flush logs).
await mcpHost.StopAsync(TimeSpan.FromSeconds(5));

return httpResult;

// ======================================================================
// Local helpers — dev-mode checks (kept local to the entry point)
// ======================================================================
static bool IsDevMode()
{
    var probe = FindRepoRootWithPnpm();
    return probe is not null && !IsPortListening(4427);
}

static bool IsPortListening(int port)
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

static string? FindRepoRootWithPnpm()
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