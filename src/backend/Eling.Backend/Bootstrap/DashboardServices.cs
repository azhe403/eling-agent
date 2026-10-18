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
using Eling.Backend.Updates;
using Eling.Core;
using Eling.Core.Codebase;
using Eling.Core.Database;
using Eling.Core.Memory;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

public static class DashboardServices
{
    public static void Register(
        IServiceCollection services,
        ProjectContext context,
        AppServices? shared = null,
        bool isOwnerMode = true)
    {
        services.AddElingLogging(projectId: ProjectId.FromScope(context.ProjectScope, context.IsUserHome));
        services.AddElingCoreServices(context.Chain, context.UserScope);

        services.TryAddSingleton(sp =>
        {
            var root = context.Chain.Cwd;
            var dbPath = Eling.Core.Scope.ElingPaths.ResolveCodebaseDbPath(root);
            return new CodebaseIndexService(root, new SqliteCodebaseIndex(dbPath));
        });

        services.TryAddSingleton<Eling.Core.Codebase.CodebaseFileReader>();

        services.TryAddSingleton<Services.ProjectScopeCatalog>();
        services.TryAddSingleton<Services.ProcessMetricsTracker>();

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
        }
        else
        {
            services.TryAddSingleton(sp => ActivatorUtilities.CreateInstance<Eling.Core.Projects.JsonWorkspaceRegistry>(
                sp,
                Path.Combine(globalAgentDir, "agent-workspaces.json")));
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

        services.AddSingleton(sp => new AppSettingsConnectionScanner(
            Environment.GetEnvironmentVariable("ELING_DATABASE_DETECT_SOURCE")
                ?? Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "appsettings.json"),
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
