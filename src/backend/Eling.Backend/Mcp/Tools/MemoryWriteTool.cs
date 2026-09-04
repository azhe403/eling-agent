using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// MCP tools for the write-side of the memory store: create, update, delete.
/// </summary>
[McpServerToolType]
public sealed class MemoryWriteTool
{
    private readonly IMemoryService _memory;
    private readonly IScopedMemoryService? _scoped;
    private readonly IMemoryChangeNotifier _notifier;
    private readonly ILogger<MemoryWriteTool>? _logger;

    private bool HasScoped => _scoped is not null;

    public MemoryWriteTool(IMemoryService memory, ILogger<MemoryWriteTool>? logger = null, IMemoryChangeNotifier? notifier = null)
    {
        _memory = memory;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
    }

    public MemoryWriteTool(IScopedMemoryService scoped, ILogger<MemoryWriteTool>? logger = null, IMemoryChangeNotifier? notifier = null)
    {
        _scoped = scoped;
        _memory = scoped.ProjectService;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
    }

    public MemoryWriteTool(IMemoryService memory, IScopedMemoryService scoped, ILogger<MemoryWriteTool>? logger = null, IMemoryChangeNotifier? notifier = null)
    {
        _memory = memory;
        _scoped = scoped;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
    }

    [McpServerTool(Name = "memory_save"), Description("Save a memory to the knowledge store. The content is the main text to remember, and optional tags help with categorization.")]
    public async Task<SaveMemoryResponse> SaveAsync(
        [Description("The content to remember")] string content,
        [Description("Type of memory: fact, preference, decision, lesson, note. Defaults to 'fact'.")] string type = "fact",
        [Description("Optional tags for categorization")] string[]? tags = null,
        [Description("Optional source reference")] string? source = null,
        [Description("Scope: project, global, or auto. Defaults to 'project'.")] string scope = "project")
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger?.LogWarning("memory_save failed: content is empty");
            throw new ArgumentException("Content cannot be empty.", nameof(content));
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            type = "fact";
        }

        if (!Enum.TryParse<MemoryType>(type, ignoreCase: true, out var memoryType))
        {
            _logger?.LogWarning("memory_save failed: invalid type '{Type}'", type);
            throw new ArgumentException($"Invalid memory type '{type}'. Valid types: {string.Join(", ", Enum.GetNames<MemoryType>())}", nameof(type));
        }

        var memory = new Memory(memoryType, content, tags, source);
        if (HasScoped && _scoped is not null)
        {
            var scoped = await _scoped.SaveAsync(memory, scope);
            _logger?.LogInformation("Saved memory '{Id}' with action '{Action}' scope '{Scope}' type '{Type}'", scoped.Id, scoped.Action, scoped.Memory.Type, scoped.Scope);
            await _notifier.NotifyAsync("mcp");
            return SaveMemoryResponse.From(scoped);
        }

        var saved = await _memory.SaveAsync(memory);
        _logger?.LogInformation("Saved memory '{Id}' with action '{Action}' type '{Type}' and {TagCount} tags", saved.Id, saved.Action, saved.Type, saved.Tags.Count);
        await _notifier.NotifyAsync("mcp");
        return SaveMemoryResponse.From(saved, scope);
    }

    [McpServerTool(Name = "memory_update"), Description("Update an existing memory by ID. Only provided fields are changed; omitted fields remain unchanged.")]
    public async Task<Memory?> UpdateAsync(
        [Description("The ULID of the memory to update")] string id,
        [Description("New content (omitted = unchanged)")] string? content = null,
        [Description("New type: fact, preference, decision, lesson, note (omitted = unchanged)")] string? type = null,
        [Description("New tags (omitted = unchanged)")] string[]? tags = null,
        [Description("New source (omitted = unchanged)")] string? source = null,
        [Description("New status: active, superseded, archived (omitted = unchanged)")] string? status = null,
        [Description("Scope: project or global. Defaults to 'project'.")] string scope = "project")
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger?.LogWarning("memory_update failed: id is empty");
            throw new ArgumentException("Id cannot be empty.", nameof(id));
        }

        var memoryId = MemoryId.Parse(id);
        MemoryType? memoryType = null;
        MemoryStatus? memoryStatus = null;

        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<MemoryType>(type, ignoreCase: true, out var parsedType))
            {
                _logger?.LogWarning("memory_update failed: invalid type '{Type}'", type);
                throw new ArgumentException($"Invalid memory type '{type}'. Valid types: {string.Join(", ", Enum.GetNames<MemoryType>())}", nameof(type));
            }
            memoryType = parsedType;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<MemoryStatus>(status, ignoreCase: true, out var parsedStatus))
            {
                _logger?.LogWarning("memory_update failed: invalid status '{Status}'", status);
                throw new ArgumentException($"Invalid memory status '{status}'. Valid statuses: {string.Join(", ", Enum.GetNames<MemoryStatus>())}", nameof(status));
            }
            memoryStatus = parsedStatus;
        }

        if (HasScoped && _scoped is not null)
        {
            var scopeKind = scope.Trim().ToLowerInvariant() == "global" ? MemoryScopeKind.Global : MemoryScopeKind.Project;
            var reference = new MemoryReference(memoryId, scopeKind, _scoped.ProjectRoot);
            var scopedUpdated = await _scoped.UpdateAsync(reference, content, memoryType, tags, source, memoryStatus);
            if (scopedUpdated is null)
            {
                _logger?.LogWarning("memory_update failed: memory '{Id}' not found in scope '{Scope}'", id, scopeKind);
                return null;
            }
            _logger?.LogInformation("Updated memory '{Id}' in scope '{Scope}'", scopedUpdated.Id, scopedUpdated.Scope);
            await _notifier.NotifyAsync("mcp");
            return scopedUpdated.Memory;
        }

        var updated = await _memory.UpdateAsync(memoryId, content, memoryType, tags, source, memoryStatus);
        if (updated is null)
        {
            _logger?.LogWarning("memory_update failed: memory '{Id}' not found", id);
            return null;
        }

        _logger?.LogInformation("Updated memory '{Id}' with type '{Type}' and {TagCount} tags", updated.Id, updated.Type, updated.Tags.Count);
        await _notifier.NotifyAsync("mcp");
        return updated;
    }

    [McpServerTool(Name = "memory_delete"), Description("Delete a memory by its ID. Returns true if the memory was deleted, false if it was not found.")]
    public async Task<bool> DeleteAsync(
        [Description("The ULID of the memory to delete")] string id,
        [Description("Scope: project or global. Defaults to 'project'.")] string scope = "project")
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger?.LogWarning("memory_delete failed: id is empty");
            throw new ArgumentException("Id cannot be empty.", nameof(id));
        }

        var memoryId = MemoryId.Parse(id);
        if (HasScoped && _scoped is not null)
        {
            var scopeKind = scope.Trim().ToLowerInvariant() == "global" ? MemoryScopeKind.Global : MemoryScopeKind.Project;
            var reference = new MemoryReference(memoryId, scopeKind, scopeKind == MemoryScopeKind.Project ? _scoped.ProjectRoot : null);
            var deleted = await _scoped.DeleteAsync(reference);
            _logger?.LogInformation("Deleted memory '{Id}' scope '{Scope}' (result: {Result})", id, scopeKind, deleted);
            if (deleted) await _notifier.NotifyAsync("mcp");
            return deleted;
        }

        var result = await _memory.DeleteAsync(memoryId);
        _logger?.LogInformation("Deleted memory '{Id}' (result: {Result})", id, result);
        if (result) await _notifier.NotifyAsync("mcp");
        return result;
    }
}

