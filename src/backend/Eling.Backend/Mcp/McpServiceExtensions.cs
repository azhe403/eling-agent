using Eling.Backend.Bootstrap;
using Eling.Backend.Codebase;
using Eling.Backend.FileSystem;
using Eling.Backend.Judging;
using Eling.Backend.Mcp.Filters;
using Eling.Backend.Mcp.Telemetry;
using Eling.Backend.Scope;
using Eling.Backend.Tools;
using Eling.Backend.Updates;
using Eling.Core;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
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

        // The audit trail (design §6.1) is registered per host by
        // AddElingAuditTrail, not here — see the note on that method.

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
            return new ScopedMemoryService(projectService, globalService, policy, merger, projectScope.Root, localService, localCanonicalRoot, auditLogger: sp.GetService<IAuditLogger>());
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

        // The audit trail is a host concern (AddElingAuditTrail): it resolves the
        // machine-global store, which a core-graph-only harness must not touch.

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

        // The recall-overflow summarizer is the same provider-backed judge: one
        // configured provider serves both the save-time judgement and the
        // recall digest, and an unconfigured provider fails the same cheap way,
        // leaving the deterministic digest in charge.
        services.TryAddSingleton<ISemanticSummarizer>(sp =>
            (ProviderSemanticJudge)sp.GetRequiredService<ISemanticJudge>());

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
            return new ScopedMemoryService(levels, globalService, policy, merger, chain.Cwd, localService: localService, canonicalRoot: localCanonicalRoot, auditLogger: sp.GetService<IAuditLogger>());
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
        UpdateServices.Add(services, userScope);

        return services;
    }

    /// <summary>
    /// Registers the audit trail's writer for one host container: the JSONL file
    /// store plus its rebuildable SQLite cache over the single machine-global
    /// audit directory (design §6.1), batched by <see cref="BufferedAuditLogger"/>.
    /// </summary>
    /// <remarks>
    /// Host-level rather than part of <c>AddElingCoreServices</c> on purpose: the
    /// store root resolves through <see cref="CentralAuditDirectory"/> and
    /// <c>ElingPaths</c>, which honours <c>ELING_DATA_DIR</c> for the hosts that
    /// set it (amendment A2) but falls back to the real machine-global store when
    /// nothing does. A unit-test harness that builds only the core graph — the
    /// scope-chain DI tests, the agent-tool tests, the MCP tool tests — runs with
    /// that variable unset, and resolving the trail there would write test noise
    /// into the real store: the exact failure class amendment A2 was written
    /// about. Hosts therefore opt in explicitly: the MCP stdio host
    /// (<c>McpHostBuilder</c>) and the dashboard owner's HTTP host
    /// (<c>HttpLoop.BuildWebApplication</c>). Both write one trail; each
    /// container owns its own writer, which the store's process-wide write gate
    /// serializes (design §7.2).
    /// </remarks>
    public static IServiceCollection AddElingAuditTrail(this IServiceCollection services)
    {
        // One writer, one index, one container. The read API's endpoints
        // resolve the SAME cache the writer batches into, so a query sees what
        // the writer wrote — and the index is registered unconditionally: an
        // unregistered IAuditIndex parameter makes minimal-API route mapping
        // infer it as a request body (GET cannot have one) and fail the host
        // at startup, which is exactly how 62 host-based tests failed before
        // this registration existed.
        services.TryAddSingleton(_ => CreateAuditWriter());
        services.TryAddSingleton<IAuditLogger>(sp =>
        {
            var writer = sp.GetRequiredService<AuditTrailWriter>();
            return new DisposableAuditLogger(
                new BufferedAuditLogger(writer.Store, writer.Index, new AuditOptions(), TimeProvider.System));
        });
        services.TryAddSingleton<IAuditIndex>(sp =>
            sp.GetRequiredService<AuditTrailWriter>().Index ?? throw new InvalidOperationException(
                "The audit index failed to initialize; the audit read API has no cache to answer from."));
        return services;
    }

    /// <summary>
    /// One container's audit trail: the file store and its rebuildable cache,
    /// shared by the writer (above) and the read API. A nested record so no
    /// file-scoped helper type is needed and no tuple appears.
    /// </summary>
    private sealed record AuditTrailWriter(
        AuditFileStore Store,
        IAuditIndex? Index);

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
            // Telemetry stays outermost so blocked calls are still logged
            // for audit; the policy filter rejects disabled tools before
            // the tool handler runs.
            filters.AddCallToolFilter(ToolTelemetryFilter.Create());
            // The ambient audit actor: every entry recorded underneath a tool
            // call carries the calling client (design §5, amendment A3). It sits
            // inside telemetry — which stays outermost — and outside the policy
            // filter, so a rejected call still carries the actor it was made as.
            filters.AddCallToolFilter(AuditActorFilter.Create());
            filters.AddCallToolFilter(ToolPolicyFilters.CreateCallFilter());
            filters.AddListToolsFilter(ToolPolicyFilters.CreateListFilter());
            // The first tools/list after the initialize handshake is the
            // in-process MCP-ready milestone for the startup stopwatch.
            filters.AddListToolsFilter(StartupTimingFilter.Create());
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
    /// Creates the audit trail's single writer over the machine-global store
    /// (design §6.1): the JSONL file store plus its rebuildable SQLite cache,
    /// batched by <see cref="BufferedAuditLogger"/>. The store root resolves
    /// through <see cref="CentralAuditDirectory"/>, so the <c>ELING_DATA_DIR</c>
    /// override isolates a test scope exactly as it does for logs (amendment A2).
    /// </summary>
    /// <remarks>
    /// The host's container owns the singleton's lifetime and disposes it at
    /// shutdown, which drains the flusher channel — both hosts dispose their
    /// provider (the MCP host on host disposal, the dashboard owner per HTTP
    /// host attempt in <see cref="HttpLoop"/>), so the change trail's pending
    /// entries always land. The logger is wrapped in
    /// <see cref="DisposableAuditLogger"/> because the engine drains only
    /// asynchronously, and a container disposed synchronously would otherwise
    /// refuse the singleton.
    /// </remarks>
    private static AuditTrailWriter CreateAuditWriter()
    {
        var auditRoot = CentralAuditDirectory.Resolve();
        var store = new AuditFileStore(auditRoot, TimeProvider.System);
        var index = new SqliteAuditIndex(auditRoot, store);

        // The index is a cache that starts empty and creates its tables only on
        // initialize. Left uninitialized, the logger's first flush would fail on
        // a missing table — swallowed fail-open, but the batch's per-entry
        // confirmations are set after the index write, so every change entry
        // would wait out its whole FlushTimeout instead of returning confirmed
        // (amendment A6). Initializing here, before the logger exists, is what
        // keeps the change trail durable-before-return.
        var indexed = TryInitializeIndex(index);
        return new AuditTrailWriter(store, indexed ? index : null);
    }

    private static bool TryInitializeIndex(SqliteAuditIndex index)
    {
        try
        {
            index.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            // A cache that cannot be created is not worth failing the host over:
            // the JSONL stays canonical (design §6.3) and a rebuild repopulates
            // the index later. Returning false keeps the logger on the JSONL
            // alone rather than on an index whose first insert would fail and
            // hold every change entry's confirmation for the full FlushTimeout.
            return false;
        }
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