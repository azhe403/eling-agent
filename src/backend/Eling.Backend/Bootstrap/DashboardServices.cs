using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend.Converters;
using Eling.Backend.Mcp;
using Eling.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddElingLogging(context.EffectiveDataDir);
        services.AddElingCoreServices(context.ProjectScope, context.UserScope);
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
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Converters.Add(new MemoryIdJsonConverter());
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
    }
}
