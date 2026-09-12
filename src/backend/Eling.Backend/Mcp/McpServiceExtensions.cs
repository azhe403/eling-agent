using Eling.Backend.FileSystem;
using Eling.Backend.Scope;
using Eling.Core;
using Eling.Core.FileSystem;
using Eling.Core.Memory;
using Eling.Core.Memory.Serialization;
using Eling.Core.Memory.Storage;
using Eling.Core.MemoryRecall;
using Eling.Core.Scope;
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

        // Bounded filesystem tools sandboxed to the project root.
        services.TryAddSingleton<IFileSystemService>(new FileSystemService(projectScope.Root));

        services.AddScoped<IMemoryRecallService>(sp =>
            new MemoryRecallService(
                sp.GetRequiredService<IScopedMemoryService>(),
                sp.GetRequiredService<IIntentionStorage>()));

        services.AddScoped<IMemoryMaintenanceService>(sp =>
            new MemoryMaintenanceService(
                sp.GetRequiredService<IMemoryService>(),
                sp.GetRequiredService<IMemoryIndex>()));

        services.TryAddSingleton<IProjectScopePolicyStore>(sp =>
            new JsonProjectScopePolicyStore(userScope, logger: sp.GetService<ILogger<JsonProjectScopePolicyStore>>()));

        return services;
    }

    public static IServiceCollection AddElingCoreServices(this IServiceCollection services, ProjectScope projectScope, UserScope userScope)
        => services.AddElingCoreServices(new ScopeChain(projectScope.Root, [projectScope]), userScope);

    /// <summary>
    /// Registers the memory graph from an ordered scope chain: one storage pair
    /// per chain level plus the global store. The head level is the write
    /// target; an uninitialized (empty) chain registers no project levels, so
    /// project writes fail with <see cref="ProjectScopeNotInitializedException"/>
    /// until the user approves <c>memory_init_project</c>. Storage instances are
    /// lazy — nothing touches disk until an actual read/write.
    /// </summary>
    public static IServiceCollection AddElingCoreServices(this IServiceCollection services, ScopeChain chain, UserScope userScope)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(userScope);

        // Global storage under UserScope (real global scope, no project runtime required)
        services.AddKeyedSingleton<IMemoryStorage>("global", (sp, key) => new FileSystemMemoryStorage(userScope.GlobalDataDirectory));
        services.AddKeyedSingleton<IMemoryIndex>("global", (sp, key) => new SqliteMemoryIndex(Path.Combine(userScope.GlobalDataDirectory, "index.db")));

        // Scope policy & merger — application layer owns scope decisions
        services.AddSingleton<IMemoryScopePolicy, MemoryScopePolicy>();
        services.AddSingleton<IMemoryMerger, MemoryMerger>();
        services.TryAddSingleton<IMemoryChangeNotifier>(NullMemoryChangeNotifier.Instance);

        // Back-compat non-scoped surface: head level storage when initialized,
        // otherwise an uninitialized path value (never created on disk).
        var headDataDirectory = chain.Head?.DataDirectory
            ?? Path.Combine(chain.Cwd, ProjectScope.DataDirectoryName);
        services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(headDataDirectory));
        services.AddSingleton<IMemoryIndex>(new SqliteMemoryIndex(Path.Combine(headDataDirectory, "index.db")));
        services.AddSingleton<IIntentionStorage>(new FileSystemIntentionStorage(headDataDirectory));
        services.AddScoped<IMemoryService, MemoryService>();

        // Scoped service: chain levels + Global, with level-grouped merge
        services.AddScoped<IScopedMemoryService>(sp =>
        {
            var policy = sp.GetRequiredService<IMemoryScopePolicy>();
            var merger = sp.GetRequiredService<IMemoryMerger>();
            var globalStorage = sp.GetRequiredKeyedService<IMemoryStorage>("global");
            var globalIndex = sp.GetRequiredKeyedService<IMemoryIndex>("global");
            var globalService = new MemoryService(globalStorage, globalIndex);
            var levels = new List<ProjectLevel>();
            foreach (var level in chain.Levels)
            {
                var service = new MemoryService(
                    new FileSystemMemoryStorage(level.DataDirectory),
                    new SqliteMemoryIndex(Path.Combine(level.DataDirectory, "index.db")));
                levels.Add(new ProjectLevel(level, service));
            }
            return new ScopedMemoryService(levels, globalService, policy, merger, chain.Cwd);
        });

        services.AddScoped<IMemoryRecallService>(sp =>
            new MemoryRecallService(
                sp.GetRequiredService<IScopedMemoryService>(),
                sp.GetRequiredService<IIntentionStorage>()));

        services.AddScoped<IMemoryMaintenanceService>(sp =>
            new MemoryMaintenanceService(
                sp.GetRequiredService<IMemoryService>(),
                sp.GetRequiredService<IMemoryIndex>()));

        // Same sandbox wiring for the scope-chain path: head level or cwd.
        services.TryAddSingleton<IFileSystemService>(
            new FileSystemService(chain.Head?.Root ?? chain.Cwd));

        services.TryAddSingleton<IProjectScopePolicyStore>(sp =>
            new JsonProjectScopePolicyStore(userScope, logger: sp.GetService<ILogger<JsonProjectScopePolicyStore>>()));

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