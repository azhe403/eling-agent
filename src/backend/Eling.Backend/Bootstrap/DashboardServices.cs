using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend.Agent.Infrastructure.Ai;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Eling.Backend.Agent.Services.Tools;
using Eling.Backend.Converters;
using Eling.Backend.Database;
using Eling.Backend.Identity;
using Eling.Backend.Mcp;
using Eling.Backend.Services;
using Eling.Backend.Updates;
using Eling.Core;
using Eling.Core.Codebase;
using Eling.Core.Database;
using Eling.Core.CommitLease;
using Eling.Core.Memory;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Eling.Core.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Bootstrap;

public static class DashboardServices
{
    public static void Register(
        IServiceCollection services,
        ProjectContext context,
        AppServices? shared = null,
        bool isOwnerMode = true,
        int? coordinatorPort = null)
    {
        // The host's first sink must already write the stored log format
        // (text/jsonl): reading the user-scope setting here, before any logger
        // is live, keeps every restart consistent with the last saved value.
        var initialLogFormat = new Eling.Backend.Logging.LogConfigStore(
                context.UserScope,
                NullLogger<Eling.Backend.Logging.LogConfigStore>.Instance)
            .GetFormat();
        services.AddElingLogging(
            projectId: ProjectId.FromWorkspace(context.Chain.Cwd, context.IsUserHome),
            initialFormat: initialLogFormat);
        services.AddElingCoreServices(context.Chain, context.UserScope);

        services.TryAddSingleton(sp =>
        {
            var root = context.Chain.Cwd;
            var dbPath = Eling.Core.Scope.ElingPaths.ResolveCodebaseDbPath(root);
            return new CodebaseIndexService(root, new SqliteCodebaseIndex(dbPath));
        });

        services.TryAddSingleton<Eling.Core.Codebase.CodebaseFileReader>();

        // The controller and the lease snapshot factory resolve the project
        // context from DI; the MCP host registers it the same way.
        services.TryAddSingleton(context);

        services.TryAddSingleton<Services.ProjectScopeCatalog>();
        services.TryAddSingleton<Services.ProcessMetricsTracker>();

        // The workspace tree's open/closed shape belongs to the user, not to one
        // process: both the desktop app and the dashboard read and write it here.
        services.TryAddSingleton(sp => new Eling.Core.Workspaces.WorkspaceExpansionStore(
            Path.Combine(context.UserScope.ConfigDirectory, "workspace-expansion.json"),
            sp.GetService<ILogger<WorkspaceExpansionStore>>()));

        // Per-user log format (text/jsonl) lives in <user-scope>/config/log.json,
        // shared with every MCP host. The dashboard owner mutates it through the
        // /api/logs/config endpoint; the switcher applies the change live.
        services.TryAddSingleton(sp => new Eling.Backend.Logging.LogConfigStore(
            context.UserScope,
            sp.GetService<ILogger<Eling.Backend.Logging.LogConfigStore>>()
                ?? NullLogger<Eling.Backend.Logging.LogConfigStore>.Instance));

        if (shared != null)
        {
            services.AddSingleton(shared.Registry);
            services.AddSingleton(shared.Broadcaster);
            services.AddSingleton<IMemoryChangeNotifier>(_ => shared.Broadcaster);
        }
        else
        {
            services.AddSingleton<RuntimeRegistry>();
            services.AddSingleton<MemoryChangeBroadcaster>();
            services.AddSingleton<IMemoryChangeNotifier>(sp => sp.GetRequiredService<MemoryChangeBroadcaster>());
        }

        services.AddSingleton<IMemoryScopePolicy, MemoryScopePolicy>();
        services.AddSingleton<IMemoryMerger, MemoryMerger>();
        services.AddScoped<IMemoryService>(sp =>
            sp.GetRequiredService<RuntimeRegistry>().ResolveMemoryService());

        var globalAgentDir = CentralAgentDirectory.Resolve();
        var legacyProjectDir = context.EffectiveDataDir;
        var legacyConfigDir = context.UserScope.GlobalDataDirectory;
        var isTestIsolation = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ELING_AGENT_DATA_DIR"));

        if (shared is not null)
        {
            services.AddSingleton(shared.Workspaces);
            // The same lease store instance the MCP host registers; both hosts
            // in the single-binary process share it (mirrors Workspaces).
            services.AddSingleton(shared.CommitLeaseStore);
        }
        else
        {
            services.TryAddSingleton(sp => ActivatorUtilities.CreateInstance<Eling.Core.Projects.JsonWorkspaceRegistry>(
                sp,
                Path.Combine(globalAgentDir, "agent-workspaces.json")));
            // Standalone hosts (no shared AppServices) get their own store over
            // the same machine-global file, like JsonDatabaseConnectionStore.
            // Its change notifier is the in-proc broadcaster when one exists
            // (owner mode registers it below), so same-process mutations wake
            // the SSE stream without a loopback roundtrip; a peer relays to
            // us via the coordinator bridge instead.
            services.TryAddSingleton(sp => new JsonCommitLeaseStore(
                Path.Combine(globalAgentDir, "commit-leases.json"),
                sp.GetService<Services.CommitLeaseBroadcaster>(),
                timeProvider: TimeProvider.System,
                logger: sp.GetService<ILogger<JsonCommitLeaseStore>>()));
        }

        services.AddSingleton(sp => new ProviderStore(
            globalAgentDir,
            sp.GetRequiredService<ILogger<ProviderStore>>(),
            !isTestIsolation && File.Exists(Path.Combine(legacyProjectDir, "agent-provider.json"))
                ? legacyProjectDir
                : (!isTestIsolation ? legacyConfigDir : null)));

        services.AddSingleton<IDatabaseConnectionStore>(sp => new JsonDatabaseConnectionStore(
            Path.Combine(globalAgentDir, "databases.json"),
            sp.GetRequiredService<ILogger<JsonDatabaseConnectionStore>>()));

        services.AddSingleton<IDatabaseReadCache>(sp => new JsonDatabaseReadCache(
            globalAgentDir,
            sp.GetRequiredService<ILogger<JsonDatabaseReadCache>>()));
        services.AddSingleton<IDatabaseReadService>(sp => new DatabaseReadService(
            sp.GetRequiredService<IDatabaseReadCache>(),
            sp.GetRequiredService<ILogger<DatabaseReadService>>()));

        services.AddSingleton<IRedisConnectionPool>(sp => new RedisConnectionPool());
        services.AddSingleton<IRedisReadCache>(sp => new JsonRedisReadCache(
            globalAgentDir,
            sp.GetRequiredService<ILogger<JsonRedisReadCache>>()));
        services.AddSingleton<IRedisKeyReadService>(sp => new RedisKeyReadService(
            sp.GetRequiredService<IRedisConnectionPool>(),
            sp.GetRequiredService<IRedisReadCache>(),
            sp.GetRequiredService<ILogger<RedisKeyReadService>>()));

        services.AddSingleton(sp => new AppSettingsConnectionScanner(
            AppSettingsSourceResolver.Resolve(sp.GetRequiredService<IHostEnvironment>().ContentRootPath),
            sp.GetRequiredService<ILogger<AppSettingsConnectionScanner>>()));

        services.AddSingleton<DatabaseConnectionImporter>();

        var chatDataDir = Path.Combine(globalAgentDir, "chats");
        var chatLegacyDir = Path.Combine(legacyProjectDir, "agent-chats");
        var chatLegacyDir2 = Path.Combine(legacyConfigDir, "agent-chats");
        services.AddSingleton(sp => new BackendChatStore(
            chatDataDir,
            sp.GetRequiredService<ILogger<BackendChatStore>>(),
            !isTestIsolation && Directory.Exists(chatLegacyDir) ? chatLegacyDir : (!isTestIsolation ? chatLegacyDir2 : null)));
        services.AddSingleton<BackendFileTools>();
        services.AddSingleton<HostBrowseService>();
        services.AddSingleton<IGitConfigReader, GitConfigReader>();
        services.AddSingleton<GitIdentityService>();
        services.AddHttpClient();
        services.AddScoped<IProviderClient, HttpProviderClient>();
        services.AddScoped<IChatGateway, MeaiChatGateway>();
        services.AddElingAgentTools();
        services.AddScoped<AgentTurnService>();

        if (isOwnerMode)
        {
            services.AddSingleton<RuntimeDirWatcher>(sp => new RuntimeDirWatcher(
                sp.GetRequiredService<RuntimeRegistry>().UserScope.RuntimeDirectory,
                sp.GetRequiredService<MemoryChangeBroadcaster>(),
                sp.GetRequiredService<ILogger<RuntimeDirWatcher>>()));
            services.AddSingleton<CodebaseDirWatcher>(sp => new CodebaseDirWatcher(
                Eling.Core.Scope.ElingPaths.ResolveCodebaseDir(),
                sp.GetRequiredService<MemoryChangeBroadcaster>(),
                sp.GetRequiredService<ILogger<CodebaseDirWatcher>>()));

            // The database registry is one shared file that every eling process
            // writes, so a connection added by the agent (db_import_connections),
            // by another browser tab, or by a peer has to reach the dashboard
            // without waiting for somebody to press Refresh.
            services.AddSingleton(sp => new DatabaseRegistryWatcher(
                CentralAgentDirectory.Resolve(),
                sp.GetRequiredService<MemoryChangeBroadcaster>(),
                sp.GetRequiredService<ILogger<DatabaseRegistryWatcher>>()));

            // Mirrors what AppServices.Create and the MCP host do when the
            // dashboard port is known: anything in this process that injects
            // ICommitLeaseChangeNotifier relays mutations to the owner over HTTP
            // (a self-loopback when this process owns the port). With no port
            // known, no notifier is registered — same as AppServices with an
            // unknown port. The standalone lease store above still pushes
            // in-proc through the broadcaster either way.
            if (isOwnerMode && coordinatorPort is int relayPort)
            {
                services.AddSingleton<ICommitLeaseChangeNotifier>(
                    _ => new HttpCoordinatorCommitLeaseChangeNotifier(relayPort));
            }

            // The SSE channel for the commit-lease panel: subscribers get the
            // current snapshot on connect, then a fresh one on every store
            // change (in-proc mutations via this process's notifier, or a
            // peer's mutation relayed as the "commit-lease" topic on the
            // memory broadcaster). The frame is machine-global — every
            // browser renders all checkouts and narrows selection
            // client-side — so the payload carries checkout = null with
            // per-row checkouts.
            services.AddSingleton(sp => new Services.CommitLeaseBroadcaster(
                ct => Eling.Backend.Dtos.CommitLeaseSnapshotDto.BuildAcrossCheckoutsAsync(
                    sp.GetRequiredService<ICommitLeaseStore>(),
                    branch: null,
                    TimeProvider.System,
                    ct),
                sp.GetRequiredService<ILogger<Services.CommitLeaseBroadcaster>>(),
                sp.GetService<MemoryChangeBroadcaster>()));

            services.AddSingleton<Services.RuntimeMetricsSampler>();
            services.AddSingleton(sp => new Services.RuntimeMetricsBroadcaster(
                sp.GetRequiredService<Services.RuntimeMetricsSampler>(),
                sp.GetRequiredService<ILogger<Services.RuntimeMetricsBroadcaster>>()));

            UpdateServices.Add(services, context.UserScope);
        }
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Converters.Add(new MemoryIdJsonConverter());
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var mvc = services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.Converters.Add(new MemoryIdJsonConverter());
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        if (Assembly.GetEntryAssembly() != typeof(GitIdentityService).Assembly)
        {
            mvc.AddApplicationPart(typeof(GitIdentityService).Assembly);
        }
    }
}
