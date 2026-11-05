using Eling.Backend.Database;
using Eling.Backend.Mcp;
using Eling.Core;
using Eling.Core.Database;
using Eling.Core.Memory;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

public static class McpHostBuilder
{
    /// <param name="context">The project to serve; discovered from the process when omitted.</param>
    /// <param name="dashboardPort">The coordinator port; resolved from the environment when omitted.</param>
    /// <param name="contentRoot">
    /// The directory the generic host treats as its content root - the one it
    /// would otherwise take from the process working directory. Tests point it
    /// at a scratch workspace instead of moving the process-global working
    /// directory, which xUnit shares across every collection running in
    /// parallel. Production passes nothing and keeps the default.
    /// </param>
    public static IHost Build(
        ProjectContext? context = null,
        int? dashboardPort = null,
        string? contentRoot = null)
    {
        var effectiveContext = context ?? ProjectContext.Discover();
        var effectivePort = dashboardPort ?? DashboardPort.Resolve();
        var shared = AppServices.Create(effectiveContext);

        var builder = Host.CreateDefaultBuilder();

        if (!string.IsNullOrWhiteSpace(contentRoot))
        {
            builder.UseContentRoot(contentRoot);
        }

        // The default host configuration reads appsettings.json and
        // appsettings.{Environment}.json from the content root, and the MCP host's
        // content root is the directory the server was launched in — the user's
        // workspace. That file belongs to the workspace, not to Eling, which
        // reads no configuration keys at all; a malformed one threw out of
        // builder.Build() (InvalidDataException) and killed the whole MCP server.
        // The HTTP host applies the same isolation in HttpLoop.BuildWebApplication.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddEnvironmentVariables();
        });

        builder.ConfigureServices(services =>
        {
            services.AddElingLogging(projectId: ProjectId.FromWorkspace(effectiveContext.Chain.Cwd, effectiveContext.IsUserHome));
            // This callback runs during builder.Build(), microseconds before the
            // provider is constructed — and Serilog's logger itself materializes
            // with the provider, after this point. The mark therefore measures
            // "all services registered", not "Serilog live"; the buffered-mark
            // flush at AttachLogger (hosted-services-started) is what proves
            // logging actually works.
            StartupStopwatch.Mark("services-configured");
            services.AddElingCoreServices(effectiveContext.Chain, effectiveContext.UserScope);
            // The audit trail is a host concern, not part of the core graph: this
            // host is the one that serves MCP tools, whose memory operations the
            // trail records (design §8). See AddElingAuditTrail for why it is not
            // registered inside AddElingCoreServices.
            services.AddElingAuditTrail();
            services.AddElingMcpServerStdio();

            // Database connection registry (needed by the db_* MCP tools even in
            // MCP-only/peer hosts; DashboardServices.Register covers the HTTP host).
            var globalAgentDir = CentralAgentDirectory.Resolve();
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
            // The resolver hands back the workspace directory when it holds any
            // appsettings file, so the scanner sees appsettings.json AND the
            // environment files next to it. It used to point at appsettings.json
            // alone, which is why an appsettings.Staging.json never showed up as
            // unimported here while the dashboard's REST detect already listed it.
            // The scanner reads the workspace the host opened, named here rather
            // than left to the resolver's default: that default is the process
            // working directory, a process-global value, and this host already
            // knows its workspace from the context. The content root stays the
            // fallback for a workspace that holds no appsettings file.
            services.AddSingleton(sp => new AppSettingsConnectionScanner(
                AppSettingsSourceResolver.Resolve(
                    sp.GetRequiredService<IHostEnvironment>().ContentRootPath,
                    effectiveContext.Chain.Cwd),
                sp.GetRequiredService<ILogger<AppSettingsConnectionScanner>>()));
            services.AddSingleton<DatabaseConnectionImporter>();

            services.AddSingleton(shared.Registry);
            services.AddSingleton(shared.Broadcaster);
            // MCP mutations are cross-process events. This process may be a
            // peer that serves no SSE at all — its own broadcaster then has
            // zero subscribers and the dashboard owner never learns about the
            // write. The HTTP coordinator notifier is the only channel that
            // reaches the process that owns the dashboard port (the endpoint
            // broadcasts "coordinator", which every dashboard page watches).
            // When this process owns the port the POST lands on itself and
            // still broadcasts, so peer takeover needs no re-wiring.
            services.AddSingleton<IMemoryChangeNotifier>(
                _ => new HttpCoordinatorMemoryChangeNotifier(effectivePort));
            services.AddSingleton(shared.UserScope);

            services.AddSingleton(shared);
            services.AddSingleton(effectiveContext);
            services.AddSingleton(new HttpLoopOptions(effectivePort));

            services.AddHostedService<RuntimeRegistrationService>();
            services.AddHostedService<HttpLoopService>();
            if (!effectiveContext.IsUserHome && !ElingPaths.IsCodebaseExcluded(effectiveContext.Chain.Cwd))
            {
                services.AddHostedService(sp => sp.GetRequiredService<CodebaseWatcherService>());
            }

            // Registered last on purpose: its StartAsync then runs after the
            // port-binding hosted services, and it flushes the StartupStopwatch
            // marks buffered since process entry through the DI logger.
            services.AddHostedService<StartupTimingHostedService>();
        });

        var host = builder.Build();

        StartupStopwatch.Mark("host-built");

        // The static Serilog Log is never assigned in this host, so the
        // historical Log.Information call here was silently dropped. Route the
        // startup line through the built host's logger factory instead.
        host.Services
            .GetService<ILoggerFactory>()
            ?.CreateLogger("Eling.Backend.Bootstrap.McpHostBuilder")
            .LogInformation(
                "Eling started - Opened folder: {Cwd}; Scope root: {ScopeRoot}; Effective data dir: {DataDir}; User-home session: {IsUserHome}",
                // The opened folder and the scope root are two different facts,
                // and each is reported from the one source that owns it: the
                // chain for the workspace it was opened in, the chain head for
                // the scope that owns the memories. The scope root is the head
                // and nothing else — an uninitialized workspace has no scope,
                // and saying so beats printing the workspace as if it were one.
                effectiveContext.Chain.Cwd,
                effectiveContext.Chain.Head?.Root ?? "(none)",
                effectiveContext.EffectiveDataDir,
                effectiveContext.IsUserHome);

        return host;
    }
}
