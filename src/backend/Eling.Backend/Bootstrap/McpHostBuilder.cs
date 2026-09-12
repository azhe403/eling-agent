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
            services.AddSingleton<IMemoryChangeNotifier>(_ => shared.Broadcaster);
            services.AddSingleton(shared.UserScope);

            services.AddSingleton(shared);
            services.AddSingleton(effectiveContext);
            services.AddSingleton(new HttpLoopOptions(effectivePort));

            services.AddHostedService<RuntimeRegistrationService>();
            services.AddHostedService<HttpLoopService>();
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
