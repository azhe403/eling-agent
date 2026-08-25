using Eling.Application;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Eling.Mcp;

public static class McpServiceExtensions
{
    public static IServiceCollection AddElingCoreServices(this IServiceCollection services, string rootPath = ".eling")
    {
        services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(rootPath));
        services.AddSingleton<IMemoryIndex>(new SqliteMemoryIndex(Path.Combine(rootPath, "index.db")));
        services.AddScoped<IMemoryService, MemoryService>();
        return services;
    }

    public static IServiceCollection AddElingMcpServerStdio(this IServiceCollection services)
    {
        services.AddMcpServer(options =>
        {
            options.ServerInstructions = ServerInstructions.Default;
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

        return services;
    }
}