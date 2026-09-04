using System.ComponentModel;
using Eling.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// MCP tools for moving memories between scopes (copy / promote).
/// </summary>
[McpServerToolType]
public sealed class MemoryPromoteTool
{
    private readonly IScopedMemoryService _scoped;
    private readonly IMemoryChangeNotifier _notifier;
    private readonly ILogger<MemoryPromoteTool>? _logger;

    public MemoryPromoteTool(IScopedMemoryService scoped, ILogger<MemoryPromoteTool>? logger = null, IMemoryChangeNotifier? notifier = null)
    {
        _scoped = scoped;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
        _logger = logger;
    }

    [McpServerTool(Name = "memory_copy_to_project"), Description("Copy a global memory to the current project. Source remains unchanged.")]
    public async Task<Memory?> CopyToProjectAsync(
        [Description("The ULID of the memory to copy")] string id,
        [Description("Source scope: global or project. Defaults to 'global'.")] string sourceScope = "global")
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id cannot be empty.", nameof(id));
        var memoryId = MemoryId.Parse(id);
        var sourceKind = sourceScope.Trim().ToLowerInvariant() == "project" ? MemoryScopeKind.Project : MemoryScopeKind.Global;
        var source = new MemoryReference(memoryId, sourceKind, sourceKind == MemoryScopeKind.Project ? _scoped.ProjectRoot : null);
        var copied = await _scoped.CopyToProjectAsync(source, _scoped.ProjectRoot!);
        if (copied is not null) await _notifier.NotifyAsync("mcp");
        return copied?.Memory;
    }

    [McpServerTool(Name = "memory_promote_to_global"), Description("Promote a project memory to global. Source remains unchanged.")]
    public async Task<Memory?> PromoteToGlobalAsync(
        [Description("The ULID of the memory to promote")] string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id cannot be empty.", nameof(id));
        var memoryId = MemoryId.Parse(id);
        var source = new MemoryReference(memoryId, MemoryScopeKind.Project, _scoped.ProjectRoot);
        var promoted = await _scoped.PromoteToGlobalAsync(source);
        if (promoted is not null) await _notifier.NotifyAsync("mcp");
        return promoted?.Memory;
    }
}
