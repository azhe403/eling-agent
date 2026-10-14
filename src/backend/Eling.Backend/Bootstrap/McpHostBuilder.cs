using Eling.Backend.Mcp;
using Eling.Core;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
            // This callback runs during builder.Build(), microseconds before the
            // provider is constructed — and Serilog's logger itself materializes
            // with the provider, after this point. The mark therefore measures
            // "all services registered", not "Serilog live"; the buffered-mark
            // flush at AttachLogger (hosted-services-started) is what proves
            // logging actually works.
            StartupStopwatch.Mark("services-configured");
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

            // Registered last on purpose: its StartAsync then runs after the
            // port-binding hosted services, and it flushes the StartupStopwatch
            // marks buffered since process entry through the DI logger.
            services.AddHostedService<StartupTimingHostedService>();
        });

        var host = builder.Build();

        StartupStopwatch.Mark("host-built");

        // The static Serilog Log is never assigned in this host, so the
        // historical Log.Information call here was silently dropped. Route the
        // startup line through the built host's logger factory instead.
        host.Services
            .GetService<ILoggerFactory>()
            ?.CreateLogger("Eling.Backend.Bootstrap.McpHostBuilder")
            .LogInformation(
                "Eling started - Opened folder: {Cwd}; Project root: {ProjectRoot}; Effective data dir: {DataDir}; User-home session: {IsUserHome}",
                Environment.CurrentDirectory,
                effectiveContext.ProjectScope.Root,
                effectiveContext.EffectiveDataDir,
                effectiveContext.IsUserHome);

        return host;
    }
}
