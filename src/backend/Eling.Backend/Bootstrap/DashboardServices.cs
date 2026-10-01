using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend.Agent.Infrastructure.Ai;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Eling.Backend.Agent.Services.Tools;
using Eling.Backend.Converters;
using Eling.Backend.Identity;
using Eling.Backend.Mcp;
using Eling.Core;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Registers the unified dependency graph: core memory/scope services,
/// MCP over stdio, runtime registry, and JSON options shared by REST + tools.
/// When a shared <see cref="AppServices"/> instance is supplied the same
/// <see cref="RuntimeRegistry"/> and <see cref="MemoryChangeBroadcaster"/> singletons
/// are reused across the MCP host and any HTTP host, so MCP-driven writes
/// are visible to REST clients and vice versa.
/// </summary>
public static class DashboardServices
{
    /// <param name="services">The <see cref="IServiceCollection"/> to configure.</param>
    /// <param name="context">Project + user scope + effective data directory.</param>
    /// <param name="shared">
    /// Optional shared singleton container. When supplied the same
    /// <see cref="RuntimeRegistry"/> and <see cref="MemoryChangeBroadcaster"/> instances
    /// are reused across the MCP host and any HTTP host. When null each host
    /// creates its own (useful when a peer already owns the port and we run MCP‑only).
    /// </param>
    /// <param name="isOwnerMode">
    /// When true (default) the HTTP host will attempt to bind Kestrel to the
    /// dashboard port. When false the HTTP host skips Kestrel and runs MCP‑only,
    /// useful when a sibling process already owns the port.
    /// </param>
    public static void Register(
        IServiceCollection services,
        ProjectContext context,
        AppServices? shared = null,
        bool isOwnerMode = true)
    {
        // Serilog to file + stderr, so stdout stays clean for MCP stdio JSON-RPC.
            services.AddElingLogging(projectId: ProjectId.FromScope(context.ProjectScope, context.IsUserHome));
        services.AddElingCoreServices(context.Chain, context.UserScope);

        // Minimal codebase index service (dashboard scope only — no embeddings)
        // Registered via TryAdd: McpServiceExtensions already provides the shared
        // singleton, this is a fallback when DashboardServices is used standalone.
        // Rooted at the working directory like the shared one, not at .eling.
        services.TryAddSingleton(sp =>
        {
            var root = context.Chain.Cwd;
            var dbPath = Eling.Core.Scope.ElingPaths.ResolveCodebaseDbPath(root);
            return new CodebaseIndexService(root, new SqliteCodebaseIndex(dbPath));
        });

        // Stateless and root-agnostic: it takes the project root per call, so a
        // single instance serves the file viewer's federated reads.
        services.TryAddSingleton<Eling.Core.Codebase.CodebaseFileReader>();

        // Codebase watcher loop runs only on the dashboard owner of a real,
        // non-excluded project session, so exactly one process indexes in
        // the background. User-home (global-only) and excluded (temporal)
        // sessions never trail their working directory. This gate is the only
        // control: the watcher is on by default, with no opt-out flag.
        if (isOwnerMode && !context.IsUserHome && !Eling.Core.Scope.ElingPaths.IsCodebaseExcluded(context.Chain.Cwd))
        {
            services.AddHostedService(sp => sp.GetRequiredService<CodebaseWatcherService>());
        }

        // NOTE: do NOT call AddElingMcpServerStdio() here. The MCP stdio transport
        // is owned exclusively by the GenericHost in Program.cs so that peer-mode
        // processes (which never build a WebApplication) still have an active
        // JSON-RPC server on stdio. Registering it here would cause the owner's
        // WebApplication to spin up a second stdio transport that fights the
        // GenericHost's for Console.OpenStandardInput/Output.

        // Shared singletons: when a container is supplied we reuse the exact same
        // RuntimeRegistry and MemoryChangeBroadcaster so MCP writes appear in
        // REST clients and vice versa. When null each host owns its own.
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

        services.AddSingleton(sp => new WorkspaceRegistry(
            Path.Combine(globalAgentDir, "agent-workspaces.json"),
            sp.GetRequiredService<ILogger<WorkspaceRegistry>>(),
            !isTestIsolation && File.Exists(Path.Combine(legacyProjectDir, "agent-workspaces.json"))
                ? Path.Combine(legacyProjectDir, "agent-workspaces.json")
                : (!isTestIsolation ? Path.Combine(legacyConfigDir, "agent-workspaces.json") : null)));

        services.AddSingleton(sp => new ProviderStore(
            globalAgentDir,
            sp.GetRequiredService<ILogger<ProviderStore>>(),
            !isTestIsolation && File.Exists(Path.Combine(legacyProjectDir, "agent-provider.json"))
                ? legacyProjectDir
                : (!isTestIsolation ? legacyConfigDir : null)));

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

        // Only the dashboard owner maps the SSE endpoint, so only it needs the
        // directory watchers. The runtime watcher turns cross-process
        // membership changes (files appearing/disappearing in the shared
        // runtime dir) into SSE "runtimes" broadcasts; the codebase watcher
        // turns any index rebuild in the shared store into "codebase"
        // broadcasts — both for every subscriber.
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
        }
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Converters.Add(new MemoryIdJsonConverter());
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        // Controllers read from a different options bag than minimal APIs, so
        // they need the same three settings mirrored here. Without this the
        // casing and converters diverge between the two styles and the same
        // DTO serializes differently depending on which one served it.
        var mvc = services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.Converters.Add(new MemoryIdJsonConverter());
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        // MVC discovers controllers from the entry assembly. Under a test
        // harness that is the test runner, so no controller is ever found and
        // every route 404s. Adding the backend assembly is skipped in the real
        // host, where the entry assembly already contributes it — registering
        // it twice would discover SystemController twice and make its route
        // ambiguous.
        if (Assembly.GetEntryAssembly() != typeof(GitIdentityService).Assembly)
        {
            mvc.AddApplicationPart(typeof(GitIdentityService).Assembly);
        }
    }
}
