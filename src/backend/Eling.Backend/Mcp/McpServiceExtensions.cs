using Eling.Backend.Bootstrap;
using Eling.Backend.Codebase;
using Eling.Backend.FileSystem;
using Eling.Backend.Judging;
using Eling.Backend.Mcp.Telemetry;
using Eling.Backend.Scope;
using Eling.Backend.Tools;
using Eling.Backend.Updates;
using Eling.Core;
using Eling.Core.Codebase;
using Eling.Core.FileSystem;
using Eling.Core.Memory;
using Eling.Core.Memory.Serialization;
using Eling.Core.Memory.Storage;
using Eling.Core.MemoryRecall;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        services.AddSingleton<IMemoryIndex>(sp => CreateMemoryIndex(dataDirectory, sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IIntentionStorage>(new FileSystemIntentionStorage(dataDirectory));
        services.AddScoped<IMemoryService, MemoryService>();

        // Global storage under UserScope (real global scope, no project runtime required)
        services.AddKeyedSingleton<IMemoryStorage>("global", (sp, key) => new FileSystemMemoryStorage(userScope.GlobalDataDirectory));
        services.AddKeyedSingleton<IMemoryIndex>("global", (sp, key) => CreateMemoryIndex(userScope.GlobalDataDirectory, sp.GetService<ILoggerFactory>()));

        // Scope policy & merger — application layer owns scope decisions
        services.AddSingleton<IMemoryScopePolicy, MemoryScopePolicy>();
        services.AddSingleton<IMemoryMerger, MemoryMerger>();
        services.TryAddSingleton<IMemoryChangeNotifier>(NullMemoryChangeNotifier.Instance);

        // Project-local storage under the central shard (machine-local,
        // worktree-shared via the canonical root). Lazy: nothing touches
        // disk until an actual read/write.
        var localCanonicalRoot = CanonicalProjectRoot.Resolve(projectScope.Root);
        var localDir = ElingPaths.ResolveProjectLocalDir(localCanonicalRoot);
        services.AddKeyedSingleton<IMemoryStorage>("project-local", (sp, key) => new FileSystemMemoryStorage(localDir));
        services.AddKeyedSingleton<IMemoryIndex>("project-local", (sp, key) => CreateMemoryIndex(localDir, sp.GetService<ILoggerFactory>()));

        // Scoped service: Project + Global + ProjectLocal, with Project priority on merge
        services.AddScoped<IScopedMemoryService>(sp =>
        {
            var policy = sp.GetRequiredService<IMemoryScopePolicy>();
            var merger = sp.GetRequiredService<IMemoryMerger>();
            var projectService = sp.GetRequiredService<IMemoryService>();
            var globalStorage = sp.GetRequiredKeyedService<IMemoryStorage>("global");
            var globalIndex = sp.GetRequiredKeyedService<IMemoryIndex>("global");
            var globalService = new MemoryService(globalStorage, globalIndex);
            var localStorage = sp.GetRequiredKeyedService<IMemoryStorage>("project-local");
            var localIndex = sp.GetRequiredKeyedService<IMemoryIndex>("project-local");
            var localService = new MemoryService(localStorage, localIndex);
            return new ScopedMemoryService(projectService, globalService, policy, merger, projectScope.Root, localService, localCanonicalRoot);
        });

        // Bounded filesystem tools sandboxed to the project root.
        services.TryAddSingleton<IFileSystemService>(new FileSystemService(projectScope.Root));

        AddCodebaseServices(services, projectScope.Root);

        services.AddScoped<IMemoryRecallService, MemoryRecallService>();
        services.AddScoped<IMemoryMaintenanceService, MemoryMaintenanceService>();

        services.TryAddSingleton<IProjectScopePolicyStore>(sp =>
            new JsonProjectScopePolicyStore(userScope, logger: sp.GetService<ILogger<JsonProjectScopePolicyStore>>()));

        services.AddSingleton(sp => new ToolPolicyStore(
            userScope,
            sp.GetService<ILoggerFactory>()?.CreateLogger<ToolPolicyStore>()
                ?? NullLogger<ToolPolicyStore>.Instance));

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
        services.AddKeyedSingleton<IMemoryIndex>("global", (sp, key) => CreateMemoryIndex(userScope.GlobalDataDirectory, sp.GetService<ILoggerFactory>()));

        // Scope policy & merger — application layer owns scope decisions
        services.AddSingleton<IMemoryScopePolicy, MemoryScopePolicy>();
        services.AddSingleton<IMemoryMerger, MemoryMerger>();
        services.TryAddSingleton<IMemoryChangeNotifier>(NullMemoryChangeNotifier.Instance);

        // Back-compat non-scoped surface: head level storage when initialized,
        // otherwise an uninitialized path value (never created on disk).
        var headDataDirectory = chain.Head?.DataDirectory
            ?? Path.Combine(chain.Cwd, ProjectScope.DataDirectoryName);
        services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(headDataDirectory));
        services.AddSingleton<IMemoryIndex>(sp => CreateMemoryIndex(headDataDirectory, sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IIntentionStorage>(new FileSystemIntentionStorage(headDataDirectory));
        services.AddScoped<IMemoryService, MemoryService>();

        // Semantic judge: a separate, per-user setting from the Desktop agent provider,
        // resolved from <user-scope>/config so both the MCP host and the dashboard read
        // the same file. Registered unconditionally so toggling it at runtime takes
        // effect without a restart; while it is unconfigured the judge throws cheaply on
        // the first call and MemoryService falls back to creating, which restores the
        // historical heuristic behaviour.
        services.AddSingleton(sp => new SemanticJudgeStore(
            userScope,
            sp.GetService<ILoggerFactory>()?.CreateLogger<SemanticJudgeStore>()
                ?? NullLogger<SemanticJudgeStore>.Instance));
        services.TryAddSingleton<ISemanticJudge>(sp => new ProviderSemanticJudge(
            sp.GetRequiredService<SemanticJudgeStore>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<ProviderSemanticJudge>()
                ?? NullLogger<ProviderSemanticJudge>.Instance));

        // Logging falls back to a null logger so the judge stays constructible in tests
        // and in hosts with no logging configured; a real host always supplies one.
        ILogger<MemoryService> MemoryLogger(IServiceProvider sp) =>
            sp.GetService<ILoggerFactory>()?.CreateLogger<MemoryService>()
            ?? NullLogger<MemoryService>.Instance;

        // Project-local storage under the central shard (machine-local,
        // worktree-shared via the canonical root). Lazy: nothing touches
        // disk until an actual read/write.
        var localCanonicalRoot = CanonicalProjectRoot.Resolve(chain.Cwd);
        var localDir = ElingPaths.ResolveProjectLocalDir(localCanonicalRoot);
        services.AddKeyedSingleton<IMemoryStorage>("project-local", (sp, key) => new FileSystemMemoryStorage(localDir));
        services.AddKeyedSingleton<IMemoryIndex>("project-local", (sp, key) => CreateMemoryIndex(localDir, sp.GetService<ILoggerFactory>()));

        // Scoped service: chain levels + Global + ProjectLocal, with level-grouped merge
        services.AddScoped<IScopedMemoryService>(sp =>
        {
            var policy = sp.GetRequiredService<IMemoryScopePolicy>();
            var merger = sp.GetRequiredService<IMemoryMerger>();
            var judge = sp.GetService<ISemanticJudge>();
            var logger = MemoryLogger(sp);
            var globalStorage = sp.GetRequiredKeyedService<IMemoryStorage>("global");
            var globalIndex = sp.GetRequiredKeyedService<IMemoryIndex>("global");
            var globalService = new MemoryService(globalStorage, globalIndex, new SmartSaveOptions(), judge, logger);
            var levels = new List<ProjectLevel>();
            foreach (var level in chain.Levels)
            {
                var service = new MemoryService(
                    new FileSystemMemoryStorage(level.DataDirectory),
                    CreateMemoryIndex(level.DataDirectory, sp.GetService<ILoggerFactory>()),
                    new SmartSaveOptions(),
                    judge,
                    logger);
                levels.Add(new ProjectLevel(level, service));
            }
            var localStorage = sp.GetRequiredKeyedService<IMemoryStorage>("project-local");
            var localIndex = sp.GetRequiredKeyedService<IMemoryIndex>("project-local");
            var localService = new MemoryService(localStorage, localIndex, new SmartSaveOptions(), judge, logger);
            return new ScopedMemoryService(levels, globalService, policy, merger, chain.Cwd, localService: localService, canonicalRoot: localCanonicalRoot);
        });

        services.AddScoped<IMemoryRecallService, MemoryRecallService>();
        services.AddScoped<IMemoryMaintenanceService, MemoryMaintenanceService>();

        // Same sandbox wiring for the scope-chain path: head level or cwd.
        services.TryAddSingleton<IFileSystemService>(
            new FileSystemService(chain.Head?.Root ?? chain.Cwd));

        // The codebase index is rooted at the working directory — the
        // workspace the backend was launched in — never at the nearest
        // .eling ancestor. Memory scope follows .eling; code follows where
        // the agent actually works.
        AddCodebaseServices(services, chain.Cwd);

        services.TryAddSingleton<IProjectScopePolicyStore>(sp =>
            new JsonProjectScopePolicyStore(userScope, logger: sp.GetService<ILogger<JsonProjectScopePolicyStore>>()));

        services.AddSingleton(sp => new ToolPolicyStore(
            userScope,
            sp.GetService<ILoggerFactory>()?.CreateLogger<ToolPolicyStore>()
                ?? NullLogger<ToolPolicyStore>.Instance));

        // Update-check graph (cache readers only — no background pump here, so MCP
        // peers never fetch; the dashboard owner writes the shared cache file).
        services.TryAddSingleton(sp => new FileUpdateCache(
            userScope,
            sp.GetService<ILoggerFactory>()?.CreateLogger<FileUpdateCache>()
                ?? NullLogger<FileUpdateCache>.Instance));
        services.AddHttpClient<GitHubReleaseClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.TryAddSingleton<IUpdateChecker>(sp => new UpdateChecker(
            sp.GetRequiredService<GitHubReleaseClient>(),
            sp.GetRequiredService<FileUpdateCache>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<UpdateChecker>()
                ?? NullLogger<UpdateChecker>.Instance,
            UpdateChecker.ResolveCurrentVersion()));

        return services;
    }

    public static IServiceCollection AddElingMcpServerStdio(this IServiceCollection services)
    {
        services.AddMcpServer(options =>
        {
            options.ServerInstructions = ServerInstructions.Default;
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(ToolTelemetryFilter.Create());
        });

        return services;
    }

    /// <summary>
    /// Registers the codebase services for one workspace root: the index
    /// (global store, per-workspace DB file), the watcher singleton, and the
    /// dashboard's multi-project rebuild job with its progress channel.
    /// The watcher loop itself starts only on the dashboard owner.
    /// Single home for both <c>AddElingCoreServices</c> overloads.
    /// </summary>
    private static void AddCodebaseServices(IServiceCollection services, string projectRoot)
    {
        services.TryAddSingleton(sp => new CodebaseIndexService(
            projectRoot,
            new SqliteCodebaseIndex(
                Eling.Core.Scope.ElingPaths.ResolveCodebaseDbPath(projectRoot),
                logger: sp.GetService<ILogger<SqliteCodebaseIndex>>()),
            sp.GetService<ILogger<CodebaseIndexService>>()));
        services.TryAddSingleton<CodebaseWatcherService>();
        services.TryAddSingleton<CodebaseRebuildBroadcaster>();
        services.TryAddSingleton<CodebaseRebuildJobRunner>();
    }

    /// <summary>
    /// Creates the memory FTS index for a data directory, migrating a legacy
    /// <c>index.db</c> (pre-rename) on first use so existing installs keep
    /// their memory search index. Migration failures (locked -wal, permissions)
    /// fall back to a fresh <c>memory.db</c> so boot never blocks.
    /// </summary>
    private static IMemoryIndex CreateMemoryIndex(string dataDirectory, ILoggerFactory? loggerFactory = null)
    {
        MigrateLegacyIndexDb(dataDirectory, loggerFactory);
        return new SqliteMemoryIndex(Path.Combine(dataDirectory, "memory.db"));
    }

    private static void MigrateLegacyIndexDb(string dataDirectory, ILoggerFactory? loggerFactory)
    {
        var memory = Path.Combine(dataDirectory, "memory.db");
        var legacy = Path.Combine(dataDirectory, "index.db");
        if (File.Exists(memory) || !File.Exists(legacy))
        {
            return;
        }

        var logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger(nameof(MigrateLegacyIndexDb));
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = legacy + suffix;
            if (!File.Exists(source))
            {
                continue;
            }

            try
            {
                File.Move(source, memory + suffix, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The install still works — memory.db is created fresh and the
                // index is rebuilt on first use — but the user has to be told,
                // because their existing memories stop being searchable until
                // then, which looks exactly like data loss and is not.
                logger.LogWarning(
                    ex,
                    "Could not migrate legacy memory index '{Source}' to '{Target}'. Memories are intact, "
                    + "but the search index starts empty and is rebuilt on first use.",
                    source,
                    memory + suffix);
            }
        }
    }
}