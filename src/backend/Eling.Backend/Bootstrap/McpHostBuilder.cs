using Eling.Backend.Mcp;
using Eling.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Eling.Backend.Bootstrap;

public static class McpHostBuilder
{
    public static IHost Build(AppServices shared, ProjectContext context)
    {
        var builder = Host.CreateDefaultBuilder();

        builder.ConfigureServices(services =>
        {
            services.AddElingLogging(context.EffectiveDataDir);
            services.AddElingCoreServices(context.ProjectScope, context.UserScope);
            services.AddElingMcpServerStdio();

            services.AddSingleton(shared.Registry);
            services.AddSingleton(shared.Broadcaster);
            services.AddSingleton<IMemoryChangeNotifier>(_ => shared.Broadcaster);
            services.AddSingleton(shared.UserScope);
        });

        return builder.Build();
    }
}
