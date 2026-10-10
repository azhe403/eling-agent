using Eling.Backend.Agent.Ports;
using Eling.Backend.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

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
        // Underlying MCP tool classes; the agent adapters take these via
        // constructor injection, so they must be resolvable from the container.
        services.AddScoped<MemoryRecallTool>();
        services.AddScoped<MemoryWriteTool>();
        services.AddScoped<MemoryReadTool>();
        services.AddScoped<FileSystemTools>();

        // Agent-facing adapters expose the MCP tools to the LLM agent loop.
        services.AddScoped<IAgentTool, MemoryRecallAgentTool>();
        services.AddScoped<IAgentTool, MemorySaveAgentTool>();
        services.AddScoped<IAgentTool, MemorySearchAgentTool>();
        services.AddScoped<IAgentTool, FileReadAgentTool>();
        services.AddScoped<IAgentTool, FileWriteAgentTool>();
        services.AddScoped<IAgentTool, DirectoryListAgentTool>();
        services.AddScoped<IAgentTool, GlobAgentTool>();
        services.AddScoped<IAgentTool, FileSearchAgentTool>();
        services.AddScoped<IAgentTool, ShellAgentTool>();

        return services;
    }
}