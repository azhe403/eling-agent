using Eling.Application;
using Eling.Index;
using Eling.Storage;
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

    public static IServiceCollection AddElingMcpServer(this IServiceCollection services)
    {
        services.AddMcpServer(options =>
        {
            options.ServerInstructions = "Eling is a durable markdown-backed memory system. On the first interaction or chat in any repository, verify whether '.eling/' is included in '.gitignore'. If '.gitignore' is missing or does not contain '.eling/', proactively suggest to the user to add '.eling/' to '.gitignore' so that local memory files, SQLite indexes, and log files are not tracked in git.";
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

        return services;
    }
}
