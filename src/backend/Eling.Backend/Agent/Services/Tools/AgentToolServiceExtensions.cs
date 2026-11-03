using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Database;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Agent.Services.Tools;

/// <summary>
/// AOT/trimming-safe registration for the built-in agent tools. Every tool
/// and adapter is registered with explicit, statically-resolvable types so
/// the Native AOT compiler and the IL trimmer can see each constructor and
/// dependency at build time. No runtime reflection scan is involved.
/// </summary>
public static class AgentToolServiceExtensions
{
    public static IServiceCollection AddElingAgentTools(this IServiceCollection services)
    {
        // Fallback registrations for database dependencies when running in minimal test/isolated contexts.
        services.TryAddSingleton<IDatabaseConnectionStore>(sp => new JsonDatabaseConnectionStore(
            Path.Combine(CentralAgentDirectory.Resolve(), "databases.json"),
            sp.GetService<ILogger<JsonDatabaseConnectionStore>>() ?? NullLogger<JsonDatabaseConnectionStore>.Instance));
        services.TryAddSingleton<IDatabaseReadCache>(sp => new JsonDatabaseReadCache(
            CentralAgentDirectory.Resolve(),
            sp.GetService<ILogger<JsonDatabaseReadCache>>() ?? NullLogger<JsonDatabaseReadCache>.Instance));
        services.TryAddSingleton<IDatabaseReadService>(sp => new DatabaseReadService(
            sp.GetRequiredService<IDatabaseReadCache>(),
            sp.GetService<ILogger<DatabaseReadService>>() ?? NullLogger<DatabaseReadService>.Instance));

        // Underlying MCP tool classes; the agent adapters take these via
        // constructor injection, so they must be resolvable from the container.
        services.AddScoped<MemoryRecallTool>();
        services.AddScoped<MemoryWriteTool>();
        services.AddScoped<MemoryReadTool>();
        services.AddScoped<FileSystemTools>();
        services.AddScoped<CodebaseSearchTool>();
        services.AddScoped<CodebaseIndexTool>();
        services.AddScoped<DatabaseListConnectionsTool>();
        services.AddScoped<DatabaseGetSchemaTool>();
        services.AddScoped<DatabaseQueryTool>();
        services.AddScoped<LogTools>();

        // Agent-facing adapters expose the MCP tools to the LLM agent loop.
        services.AddScoped<IAgentTool, MemoryRecallAgentTool>();
        services.AddScoped<IAgentTool, MemorySaveAgentTool>();
        services.AddScoped<IAgentTool, MemorySearchAgentTool>();
        services.AddScoped<IAgentTool, FileReadAgentTool>();
        services.AddScoped<IAgentTool, FileWriteAgentTool>();
        services.AddScoped<IAgentTool, FileEditAgentTool>();
        services.AddScoped<IAgentTool, FileAppendAgentTool>();
        services.AddScoped<IAgentTool, FileDeleteAgentTool>();
        services.AddScoped<IAgentTool, DirectoryListAgentTool>();
        services.AddScoped<IAgentTool, DirectoryCreateAgentTool>();
        services.AddScoped<IAgentTool, GlobAgentTool>();
        services.AddScoped<IAgentTool, FileSearchAgentTool>();
        services.AddScoped<IAgentTool, PathTestAgentTool>();
        services.AddScoped<IAgentTool, CodebaseSearchAgentTool>();
        services.AddScoped<IAgentTool, CodebaseIndexAgentTool>();
        services.AddScoped<IAgentTool, DatabaseListConnectionsAgentTool>();
        services.AddScoped<IAgentTool, DatabaseGetSchemaAgentTool>();
        services.AddScoped<IAgentTool, DatabaseQueryAgentTool>();
        services.AddScoped<IAgentTool, LogListAgentTool>();
        services.AddScoped<IAgentTool, LogReadAgentTool>();
        services.AddScoped<IAgentTool, ShellAgentTool>();

        return services;
    }
}