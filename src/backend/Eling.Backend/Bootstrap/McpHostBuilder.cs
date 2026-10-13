using Eling.Backend.Mcp;
using Eling.Core;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Eling.Backend.Bootstrap;

public static class McpHostBuilder
{
    public static IHost Build(
        ProjectContext? context = null,
        int? dashboardPort = null)
    {
        var effectiveContext = context ?? ProjectContext.Discover();
        var effectivePort = dashboardPort ?? DashboardPort.Resolve();
        var shared = AppServices.Create(effectiveContext);

        var builder = Host.CreateDefaultBuilder();

        builder.ConfigureServices(services =>
        {
            services.AddElingLogging(projectId: ProjectId.FromScope(effectiveContext.ProjectScope, effectiveContext.IsUserHome));
            services.AddElingCoreServices(effectiveContext.Chain, effectiveContext.UserScope);
            services.AddElingMcpServerStdio();

            services.AddSingleton(shared.Registry);
            services.AddSingleton(shared.Broadcaster);
            // MCP mutations are cross-process events. This process may be a
            // peer that serves no SSE at all — its own broadcaster then has
            // zero subscribers and the dashboard owner never learns about the
            // write. The HTTP coordinator notifier is the only channel that
            // reaches the process that owns the dashboard port (the endpoint
            // broadcasts "coordinator", which every dashboard page watches).
            // When this process owns the port the POST lands on itself and
            // still broadcasts, so peer takeover needs no re-wiring.
            services.AddSingleton<IMemoryChangeNotifier>(
                _ => new HttpCoordinatorMemoryChangeNotifier(effectivePort));
            services.AddSingleton(shared.UserScope);

            services.AddSingleton(shared);
            services.AddSingleton(effectiveContext);
            services.AddSingleton(new HttpLoopOptions(effectivePort));

            services.AddHostedService<RuntimeRegistrationService>();
            services.AddHostedService<HttpLoopService>();
            if (!effectiveContext.IsUserHome && !ElingPaths.IsCodebaseExcluded(effectiveContext.Chain.Cwd))
            {
                services.AddHostedService(sp => sp.GetRequiredService<CodebaseWatcherService>());
            }
        });

        Log.Information(
            "Eling started - Opened folder: {Cwd}; Project root: {ProjectRoot}; Effective data dir: {DataDir}; User-home session: {IsUserHome}",
            Environment.CurrentDirectory,
            effectiveContext.ProjectScope.Root,
            effectiveContext.EffectiveDataDir,
            effectiveContext.IsUserHome);

        return builder.Build();
    }
}
