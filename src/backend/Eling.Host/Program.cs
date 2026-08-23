using Eling.Core;
using Eling.Host;
using Eling.Mcp;
using Eling.Server.Converters;
using Eling.Server.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

// ---------------------------------------------------------------------------
// Eling — Single Binary Runtime (PECUT 9)
// One user-facing command: `eling`
// Internal runtime roles: MCP (project-scoped, stdio) + Dashboard (user-scoped, HTTP 4317)
// ---------------------------------------------------------------------------

var argsList = args.ToList();
var noDashboard = argsList.Contains("--no-dashboard");
var httpMcp = argsList.Contains("--http-mcp");

// When running under WebApplicationFactory<Program>, no CLI args are passed.
// ELING_NO_DASHBOARD forces the web host path so the test factory gets a real server.
noDashboard = noDashboard || Environment.GetEnvironmentVariable("ELING_NO_DASHBOARD") == "1";

var stdioMcp = !httpMcp && !noDashboard;

// Resolve scopes FIRST — before any host creation
var projectScope = ProjectScope.Discover();
var userScope = UserScope.Resolve();

Directory.CreateDirectory(projectScope.DataDirectory);
Directory.CreateDirectory(userScope.ConfigDirectory);
Directory.CreateDirectory(userScope.RuntimeDirectory);

if (stdioMcp)
{
    RunStdioMcp(projectScope, userScope);
}
else
{
    await RunWebHostAsync(projectScope, userScope, httpMcp, noDashboard);
}

return;

// ===========================================================================
// Helpers
// ===========================================================================

static string? FindArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
            return args[i + 1];
    }
    return null;
}

static string GetDataDirectory(ProjectScope projectScope) => projectScope.DataDirectory;

// ===========================================================================
// STDIO MCP Role — Project-scoped, stdout = JSON-RPC only, logs → stderr
// ===========================================================================

static void RunStdioMcp(ProjectScope projectScope, UserScope userScope)
{
    // Fire-and-forget: never delay the MCP handshake waiting for the dashboard
    _ = Task.Run(() => EnsureDashboardAsync(userScope, projectScope));

    var hostBuilder = Host.CreateApplicationBuilder([]);
    hostBuilder.Logging.ClearProviders();
    hostBuilder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    hostBuilder.Logging.SetMinimumLevel(LogLevel.Trace);

    ConfigureServices(hostBuilder.Services, projectScope.DataDirectory, userScope: userScope, httpMcp: false, isWebHost: false);

    var host = hostBuilder.Build();
    host.Run();
}

// ===========================================================================
// Dashboard auto-start — stdio role ensures the user-scoped coordinator exists
// ===========================================================================

static async Task EnsureDashboardAsync(UserScope userScope, ProjectScope projectScope)
{
    var registration = new RuntimeRegistration
    {
        ProcessId = Environment.ProcessId,
        ProjectRoot = projectScope.Root,
        DataDirectory = projectScope.DataDirectory,
        StartTime = DateTimeOffset.UtcNow,
        McpEnabled = true,
        McpTransport = "stdio",
    };

    var coordinationFile = GetRuntimeCoordinationFile(userScope);

    if (!IsCoordinatorAlive(coordinationFile))
    {
        SpawnDashboard();
        await WaitForCoordinatorAsync();
    }

    await RegisterWithCoordinatorAsync(registration);

    // Keep the registry entry alive while this process runs
    _ = HeartbeatLoopAsync(registration.ProcessId);
}

static bool IsCoordinatorAlive(string coordinationFile)
{
    try
    {
        if (!File.Exists(coordinationFile)) return false;

        var json = File.ReadAllText(coordinationFile);
        var claim = JsonSerializer.Deserialize(json, CoordinatorJsonContext.Default.CoordinatorClaim);
        if (claim is null) return false;

        try { return !Process.GetProcessById(claim.ProcessId).HasExited; }
        catch { return false; }
    }
    catch
    {
        return false;
    }
}

static void SpawnDashboard()
{
    try
    {
        // ponytail: spawns via dotnet exec; single-file publish needs bundle extraction — revisit when publishing
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{Path.Combine(AppContext.BaseDirectory, "eling.dll")}\" --http-mcp",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(psi);
        if (process is null) return;

        // Drain pipes so the child never blocks on a full buffer
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
    }
    catch
    {
        // Dashboard is optional — MCP must survive without it
    }
}

static async Task WaitForCoordinatorAsync()
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };

    for (var i = 0; i < 10; i++)
    {
        try
        {
            using var response = await client.GetAsync("http://127.0.0.1:4317/health");
            if (response.IsSuccessStatusCode) return;
        }
        catch { /* not up yet */ }

        await Task.Delay(500);
    }
}

static async Task RegisterWithCoordinatorAsync(RuntimeRegistration registration)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var json = JsonSerializer.Serialize(registration, CoordinatorJsonContext.Default.RuntimeRegistration);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        await client.PostAsync("http://localhost:4317/api/coordinator/register", content);
    }
    catch
    {
        // Coordinator not reachable — dashboard stays absent, MCP continues
    }
}

static async Task HeartbeatLoopAsync(int pid)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

    while (true)
    {
        try
        {
            await client.PostAsync($"http://localhost:4317/api/coordinator/heartbeat/{pid}", null);
        }
        catch { /* coordinator gone — retry next tick */ }

        await Task.Delay(TimeSpan.FromSeconds(15));
    }
}

// ===========================================================================
// Web Host Role — Dashboard Coordinator + REST API + optional HTTP MCP
// ===========================================================================

static async Task RunWebHostAsync(ProjectScope projectScope, UserScope userScope, bool httpMcp, bool noDashboard)
{
    // Serve the dashboard from the folder next to the binary, regardless of cwd
    // (MCP clients may spawn us from any working directory).
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    });
    builder.Configuration.Sources.Clear();

    // Kestrel: bind only to localhost:4317
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.Listen(IPAddress.Loopback, 4317);
    });

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Trace);

    var dataDir = GetDataDirectory(projectScope);
    ConfigureServices(builder.Services, dataDir, userScope: userScope, httpMcp: httpMcp, isWebHost: true);

    var app = builder.Build();

    ConfigureApp(app, httpMcp);

    // Register this runtime with the Dashboard Coordinator
    var runtimeInfo = new RuntimeRegistration
    {
        ProcessId = Environment.ProcessId,
        ProjectRoot = projectScope.Root,
        DataDirectory = dataDir,
        StartTime = DateTimeOffset.UtcNow,
        McpEnabled = true,
        McpTransport = httpMcp ? "http" : "stdio",
    };

    var coordinator = app.Services.GetService<DashboardCoordinator>();
    if (coordinator is null)
        throw new InvalidOperationException("DashboardCoordinator not registered. Ensure ConfigureServices was called with isWebHost=true.");

    // Startup race: try to become the coordinator, otherwise register with existing
    var becameCoordinator = await coordinator.TryStartOrRegisterAsync(runtimeInfo, app, noDashboard);

    // Non-coordinator instances must heartbeat so the coordinator knows we're alive
    if (!becameCoordinator)
    {
        _ = HeartbeatLoopAsync(runtimeInfo.ProcessId);
    }

    // If we became the coordinator, we own the dashboard lifecycle
    // If not, we registered and the existing coordinator manages dashboard
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        _ = coordinator.UnregisterAsync(runtimeInfo.ProcessId);
    });

    // Dashboard Coordinator endpoints (only when we are the coordinator)
    if (becameCoordinator)
    {
        DashboardCoordinator.MapCoordinatorEndpoints(app);
    }

    // Run the web host (dashboard + REST API + optional HTTP MCP)
    await app.RunAsync();
}

// ===========================================================================
// Service Configuration (shared by both roles)
// ===========================================================================

public partial class Program
{
    public static string GetRuntimeCoordinationFile(UserScope userScope)
        => Path.Combine(userScope.RuntimeDirectory, "dashboard-coordinator.json");

    public static string GetRuntimeRegistryFile(UserScope userScope)
        => Path.Combine(userScope.RuntimeDirectory, "runtimes.json");

    public static void ConfigureServices(IServiceCollection services, string dataDirectory, UserScope userScope, bool httpMcp = false, bool isWebHost = true)
    {
        // Logging — all logs to stderr, stdout reserved for JSON-RPC
        services.AddElingLogging(dataDirectory);

        // Core domain services
        services.AddElingCoreServices(dataDirectory);

        // Register UserScope so DashboardCoordinator can be constructed via DI
        services.AddSingleton(userScope);

        // MCP server — stdio role uses stdio transport; web host optionally exposes HTTP MCP.
        // Never register stdio MCP in the web host: its stdin reader EOFs and kills the app.
        if (!isWebHost)
            services.AddElingMcpServerStdio();
        else if (httpMcp)
            services.AddElingMcpServerHttp();

        // ASP.NET Core services — only for web host
        if (isWebHost)
        {
            services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.SerializerOptions.Converters.Add(new MemoryIdJsonConverter());
                o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

            services.AddOpenApi();

            // Dashboard Coordinator — always registered
            services.AddSingleton<DashboardCoordinator>();
        }
    }

    public static void ConfigureApp(WebApplication app, bool httpMcp = false)
    {
        // Static files + SPA fallback for dashboard.
        // UseRouting is called EXPLICITLY after the static middleware: WebApplication
        // otherwise inserts it at the very start of the pipeline, so the fallback
        // endpoint would capture /dashboard/memories/ before static files could
        // serve its index.html — every deep link then rendered the root page.
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.MapFallbackToFile("index.html");

        // MCP HTTP transport endpoint (only when --http-mcp)
        if (httpMcp)
        {
            app.MapMcp();
        }

        // Health check
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

        // Memory REST API routes
        app.MapMemoryRoutes();

        // OpenAPI in development
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }
    }
}