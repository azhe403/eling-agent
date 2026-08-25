using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
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
app.MapCoordinatorEndpoints();
app.MapMemoryRoutes();
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

// The actual bind is authoritative. When several eling processes spawn a
// dashboard simultaneously, exactly one owns 127.0.0.1:4317; the losers hit
// AddressInUse here and exit cleanly without touching the winner.
try
{
    app.Run();
}
catch (Exception ex) when (IsAddressInUse(ex))
{
    Console.Error.WriteLine("[eling-dashboard] 127.0.0.1:4317 is already owned by another dashboard; exiting cleanly.");
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
