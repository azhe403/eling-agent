using System.ComponentModel;
using Eling.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// MCP tools for index maintenance on the memory store.
/// </summary>
[McpServerToolType]
public sealed class MemoryIndexTool
{
    private readonly IMemoryService _memory;
    private readonly IScopedMemoryService? _scoped;
    private readonly ILogger<MemoryIndexTool>? _logger;

    private bool HasScoped => _scoped is not null;

    public MemoryIndexTool(IMemoryService memory, ILogger<MemoryIndexTool>? logger = null)
    {
        _memory = memory;
        _logger = logger;
    }

    public MemoryIndexTool(IScopedMemoryService scoped, ILogger<MemoryIndexTool>? logger = null)
    {
        _scoped = scoped;
        _memory = scoped.ProjectService;
        _logger = logger;
    }

    public MemoryIndexTool(IMemoryService memory, IScopedMemoryService scoped, ILogger<MemoryIndexTool>? logger = null)
    {
        _memory = memory;
        _scoped = scoped;
        _logger = logger;
    }

    [McpServerTool(Name = "memory_rebuild_index"), Description("Rebuild the search index from all stored memories.")]
    public async Task RebuildIndexAsync(
        [Description("Scope: project, global, or merged. Defaults to 'merged'.")] string scope = "merged")
    {
        _logger?.LogInformation("Rebuilding memory index scope '{Scope}'", scope);
        if (HasScoped)
        {
            await _scoped!.RebuildIndexAsync(scope);
            _logger?.LogInformation("Memory index rebuilt for scope '{Scope}'", scope);
            return;
        }
        await _memory.RebuildIndexAsync();
        _logger?.LogInformation("Memory index rebuilt successfully");
    }
}
