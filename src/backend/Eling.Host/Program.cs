using Eling.Core;
using Eling.Mcp;
using Eling.Server.Converters;
using Eling.Server.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

// ---------------------------------------------------------------------------
// Top-level entry — WebApplicationFactory intercepts CreateBuilder + Run
// ---------------------------------------------------------------------------

// Pre-detect args before building any host
var rootPath = FindArg(args, "--root-path") ?? Path.Combine(RepositoryRoot.Find(), ".eling");
var httpMcp = args.Contains("--http-mcp");
var enableMcp = args.Contains("--enable-mcp");
var stdioMcp = enableMcp && !httpMcp;

if (stdioMcp)
{
    // Stdio MCP mode — generic host, no web server, no Kestrel
    var hostBuilder = Host.CreateApplicationBuilder(args);
    // CRITICAL: stdout is the JSON-RPC channel. Send all logs to stderr.
    hostBuilder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    Program.ConfigureServices(hostBuilder.Services, rootPath, enableMcp, httpMcp, isWebHost: false);
    var host = hostBuilder.Build();
    await host.RunAsync();
}
else
{
    // Web host — REST API + optional HTTP MCP
    var builder = WebApplication.CreateBuilder(args);

    // Configuration: factory injects via UseSetting("Eling:EnableMcp", "false")
    var configRootPath = FindArg(args, "--root-path")
        ?? builder.Configuration["Eling:RootPath"]
        ?? rootPath;

    enableMcp = enableMcp || string.Equals(builder.Configuration["Eling:EnableMcp"], "true", StringComparison.OrdinalIgnoreCase);

    Program.ConfigureServices(builder.Services, configRootPath, enableMcp, httpMcp, isWebHost: true);

    var app = builder.Build();

    Program.ConfigureApp(app, enableMcp, httpMcp);

    var port = int.TryParse(FindArg(args, "--port"), out var p) ? p : 4317;
    app.Urls.Add($"http://127.0.0.1:{port}");

    try
    {
        await app.RunAsync();
    }
    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
    {
        Console.Error.WriteLine("Port is already in use. Check if another instance of Eling is running on the configured port.");
    }
}

return;

// ---------------------------------------------------------------------------
// Arg helpers
// ---------------------------------------------------------------------------
static string? FindArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
            return args[i + 1];
    }
    return null;
}

// ---------------------------------------------------------------------------
// Host application factory
// ---------------------------------------------------------------------------
public partial class Program
{
    public static void ConfigureServices(IServiceCollection services, string rootPath, bool enableMcp, bool httpMcp = false, bool isWebHost = true)
    {
        // Logging (Eling-specific)
        services.AddElingLogging(rootPath);

        // Core domain services (MemoryId, MemoryService, SqliteMemoryIndex, FileSystemMemoryStorage)
        services.AddElingCoreServices(rootPath);

        // MCP server — stdio by default (for opencode), HTTP when --http-mcp
        if (enableMcp)
        {
            if (httpMcp)
                services.AddElingMcpServerHttp();
            else
                services.AddElingMcpServerStdio();
        }

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
        }
    }

    public static void ConfigureApp(WebApplication app, bool enableMcp, bool httpMcp = false)
    {
        // MCP HTTP transport endpoint (only when --http-mcp)
        if (enableMcp && httpMcp)
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
