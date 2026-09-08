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

    public MemoryPromoteTool(
        IScopedMemoryService scoped,
        ILogger<MemoryPromoteTool>? logger = null,
        IMemoryChangeNotifier? notifier = null)
    {
        _scoped = scoped;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
    }

    [McpServerTool(Name = "memory_copy_to_project"), Description("Copy a memory to the current project. Use operation='copy' to keep source, 'move' to delete source.")]
    public async Task<Memory?> CopyToProjectAsync(
        [Description("The ULID of the memory to copy")] string id,
        [Description("Source scope: global or project. Defaults to 'global'.")] string sourceScope = "global",
        [Description("Operation: 'copy' (default, keeps source) or 'move' (deletes source).")] string operation = "copy")
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id cannot be empty.", nameof(id));
        var op = operation.Trim().ToLowerInvariant();
        if (op != "copy" && op != "move")
        {
            throw new ArgumentException("Operation must be 'copy' or 'move'.", nameof(operation));
        }

        var memoryId = MemoryId.Parse(id);
        var sourceKind = sourceScope.Trim().ToLowerInvariant() == "project" ? MemoryScopeKind.Project : MemoryScopeKind.Global;
        var source = new MemoryReference(memoryId, sourceKind, sourceKind == MemoryScopeKind.Project ? _scoped.ProjectRoot : null);

        _logger?.LogInformation(
            "Memory '{Id}' {Operation} to project (sourceScope={SourceScope})",
            id, op, sourceScope);
        ScopedMemory? result;
        if (op == "move")
        {
            result = await _scoped.MoveToProjectAsync(source, _scoped.ProjectRoot!);
        }
        else
        {
            result = await _scoped.CopyToProjectAsync(source, _scoped.ProjectRoot!);
        }

        _logger?.LogInformation(
            "Memory '{Id}' {Operation}d to project (result: {Result})",
            id, op, result is not null);
        if (result is not null)
        {
            await _notifier.NotifyAsync("mcp");
            await _scoped.RebuildIndexAsync("project");
        }
        return result?.Memory;
    }

    [McpServerTool(Name = "memory_promote_to_global"), Description("Promote a project memory to global. Use operation='copy' to keep source, 'move' to delete source.")]
    public async Task<Memory?> PromoteToGlobalAsync(
        [Description("The ULID of the memory to promote")] string id,
        [Description("Operation: 'copy' (default, keeps source) or 'move' (deletes source).")] string operation = "copy")
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id cannot be empty.", nameof(id));
        var op = operation.Trim().ToLowerInvariant();
        if (op != "copy" && op != "move")
        {
            throw new ArgumentException("Operation must be 'copy' or 'move'.", nameof(operation));
        }

        var memoryId = MemoryId.Parse(id);
        var source = new MemoryReference(memoryId, MemoryScopeKind.Project, _scoped.ProjectRoot);

        _logger?.LogInformation(
            "Memory '{Id}' {Operation} to global",
            id, op);
        ScopedMemory? result;
        if (op == "move")
        {
            result = await _scoped.MoveToGlobalAsync(source);
        }
        else
        {
            result = await _scoped.CopyToGlobalAsync(source);
        }

        _logger?.LogInformation(
            "Memory '{Id}' {Operation}d to global (result: {Result})",
            id, op, result is not null);
        if (result is not null)
        {
            await _notifier.NotifyAsync("mcp");
            await _scoped.RebuildIndexAsync("global");
        }
        return result?.Memory;
    }
}
