using Eling.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp;

public static class McpServiceExtensions
{
    public static IServiceCollection AddElingCoreServices(this IServiceCollection services, string rootPath = ".eling")
    {
        // rootPath is the data directory (e.g. ".eling" or "/projects/a/.eling")
        var normalizedDataDir = Path.GetFullPath(rootPath);
        var projectRoot = normalizedDataDir.EndsWith(ProjectScope.DataDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(normalizedDataDir) ?? normalizedDataDir
            : normalizedDataDir;
        var projectScope = new ProjectScope(projectRoot);
        // Ensure the requested data directory is the one we use (support temp/test dirs)
        var dataDirectory = normalizedDataDir;
        var userScope = UserScope.Resolve(Environment.GetEnvironmentVariable("ELING_USER_SCOPE"));

        // Project-scoped storage (primary, backward compatible)
        services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(dataDirectory));
        services.AddSingleton<IMemoryIndex>(new SqliteMemoryIndex(Path.Combine(dataDirectory, "index.db")));
        services.AddSingleton<IIntentionStorage>(new FileSystemIntentionStorage(dataDirectory));
        services.AddScoped<IMemoryService, MemoryService>();

        // Global storage under UserScope (real global scope, no project runtime required)
        services.AddKeyedSingleton<IMemoryStorage>("global", (sp, key) => new FileSystemMemoryStorage(userScope.GlobalDataDirectory));
        services.AddKeyedSingleton<IMemoryIndex>("global", (sp, key) => new SqliteMemoryIndex(Path.Combine(userScope.GlobalDataDirectory, "index.db")));

        // Scope policy & merger — application layer owns scope decisions
        services.AddSingleton<IMemoryScopePolicy, MemoryScopePolicy>();
        services.AddSingleton<IMemoryMerger, MemoryMerger>();
        services.TryAddSingleton<IMemoryChangeNotifier>(NullMemoryChangeNotifier.Instance);

        // Scoped service: Project + Global, with Project priority on merge
        services.AddScoped<IScopedMemoryService>(sp =>
        {
            var policy = sp.GetRequiredService<IMemoryScopePolicy>();
            var merger = sp.GetRequiredService<IMemoryMerger>();
            var projectService = sp.GetRequiredService<IMemoryService>();
            var globalStorage = sp.GetRequiredKeyedService<IMemoryStorage>("global");
            var globalIndex = sp.GetRequiredKeyedService<IMemoryIndex>("global");
            var globalService = new MemoryService(globalStorage, globalIndex);
            return new ScopedMemoryService(projectService, globalService, policy, merger, projectScope.Root);
        });

        services.AddScoped<IMemoryRecallService>(sp =>
            new MemoryRecallService(
                sp.GetRequiredService<IScopedMemoryService>(),
                sp.GetRequiredService<IIntentionStorage>()));

        return services;
    }

    public static IServiceCollection AddElingCoreServices(this IServiceCollection services, ProjectScope projectScope, UserScope userScope)
    {
        ArgumentNullException.ThrowIfNull(projectScope);
        ArgumentNullException.ThrowIfNull(userScope);

        services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(projectScope.DataDirectory));
        services.AddSingleton<IMemoryIndex>(new SqliteMemoryIndex(Path.Combine(projectScope.DataDirectory, "index.db")));
        services.AddSingleton<IIntentionStorage>(new FileSystemIntentionStorage(projectScope.DataDirectory));
        services.AddScoped<IMemoryService, MemoryService>();

        services.AddKeyedSingleton<IMemoryStorage>("global", (sp, key) => new FileSystemMemoryStorage(userScope.GlobalDataDirectory));
        services.AddKeyedSingleton<IMemoryIndex>("global", (sp, key) => new SqliteMemoryIndex(Path.Combine(userScope.GlobalDataDirectory, "index.db")));

        services.AddSingleton<IMemoryScopePolicy, MemoryScopePolicy>();
        services.AddSingleton<IMemoryMerger, MemoryMerger>();
        services.TryAddSingleton<IMemoryChangeNotifier>(NullMemoryChangeNotifier.Instance);

        services.AddScoped<IScopedMemoryService>(sp =>
        {
            var policy = sp.GetRequiredService<IMemoryScopePolicy>();
            var merger = sp.GetRequiredService<IMemoryMerger>();
            var projectService = sp.GetRequiredService<IMemoryService>();
            var globalStorage = sp.GetRequiredKeyedService<IMemoryStorage>("global");
            var globalIndex = sp.GetRequiredKeyedService<IMemoryIndex>("global");
            var globalService = new MemoryService(globalStorage, globalIndex);
            return new ScopedMemoryService(projectService, globalService, policy, merger, projectScope.Root);
        });

        services.AddScoped<IMemoryRecallService>(sp =>
            new MemoryRecallService(
                sp.GetRequiredService<IScopedMemoryService>(),
                sp.GetRequiredService<IIntentionStorage>()));

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