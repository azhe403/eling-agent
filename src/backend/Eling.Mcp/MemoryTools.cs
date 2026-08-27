using System.ComponentModel;
using Eling.Application;
using Eling.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Mcp;

[McpServerToolType]
public class MemoryTools
{
    private readonly IMemoryService _memory;
    private readonly IScopedMemoryService? _scoped;
    private readonly ILogger<MemoryTools>? _logger;

    public MemoryTools(IMemoryService memory, ILogger<MemoryTools>? logger = null)
    {
        _memory = memory;
        _logger = logger;
    }

    public MemoryTools(IScopedMemoryService scoped, ILogger<MemoryTools>? logger = null)
    {
        _scoped = scoped;
        _memory = scoped.ProjectService;
        _logger = logger;
    }

    public MemoryTools(IMemoryService memory, IScopedMemoryService scoped, ILogger<MemoryTools>? logger = null)
    {
        _memory = memory;
        _scoped = scoped;
        _logger = logger;
    }

    private bool HasScoped => _scoped is not null;

    [McpServerTool(Name = "memory_save"), Description("Save a memory to the knowledge store. The content is the main text to remember, and optional tags help with categorization.")]
    public async Task<Memory> SaveAsync(
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
            _logger?.LogInformation("Saved memory '{Id}' with scope '{Scope}' type '{Type}'", scoped.Id, scoped.Scope, scoped.Memory.Type);
            return scoped.Memory;
        }

        var saved = await _memory.SaveAsync(memory);
        _logger?.LogInformation("Saved memory '{Id}' with type '{Type}' and {TagCount} tags", saved.Id, saved.Type, saved.Tags.Count);
        return saved;
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
            return scopedUpdated.Memory;
        }

        var updated = await _memory.UpdateAsync(memoryId, content, memoryType, tags, source, memoryStatus);
        if (updated is null)
        {
            _logger?.LogWarning("memory_update failed: memory '{Id}' not found", id);
            return null;
        }

        _logger?.LogInformation("Updated memory '{Id}' with type '{Type}' and {TagCount} tags", updated.Id, updated.Type, updated.Tags.Count);
        return updated;
    }

    [McpServerTool(Name = "memory_get"), Description("Retrieve a memory by its ID.")]
    public async Task<Memory?> GetByIdAsync(
        [Description("The ULID of the memory to retrieve")] string id,
        [Description("Scope: project, global, or merged. Defaults to 'project'.")] string scope = "project")
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger?.LogWarning("memory_get failed: id is empty");
            throw new ArgumentException("Id cannot be empty.", nameof(id));
        }

        var memoryId = MemoryId.Parse(id);
        if (HasScoped && scope.Trim().ToLowerInvariant() == "merged")
        {
            var projectRef = new MemoryReference(memoryId, MemoryScopeKind.Project, _scoped!.ProjectRoot);
            var found = await _scoped.GetByIdAsync(projectRef);
            if (found is not null) return found.Memory;
            var globalRef = MemoryReference.ForGlobal(memoryId);
            var global = await _scoped.GetByIdAsync(globalRef);
            _logger?.LogInformation("Retrieved memory '{Id}' merged (found: {Found})", id, global is not null);
            return global?.Memory;
        }
        if (HasScoped && _scoped is not null)
        {
            var scopeKind = scope.Trim().ToLowerInvariant() == "global" ? MemoryScopeKind.Global : MemoryScopeKind.Project;
            var reference = new MemoryReference(memoryId, scopeKind, scopeKind == MemoryScopeKind.Project ? _scoped.ProjectRoot : null);
            var scopedResult = await _scoped.GetByIdAsync(reference);
            _logger?.LogInformation("Retrieved memory '{Id}' scope '{Scope}' (found: {Found})", id, scopeKind, scopedResult is not null);
            return scopedResult?.Memory;
        }

        var result = await _memory.GetByIdAsync(memoryId);
        _logger?.LogInformation("Retrieved memory '{Id}' (found: {Found})", id, result is not null);
        return result;
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
            return deleted;
        }

        var result = await _memory.DeleteAsync(memoryId);
        _logger?.LogInformation("Deleted memory '{Id}' (result: {Result})", id, result);
        return result;
    }

    [McpServerTool(Name = "memory_list"), Description("List memories, optionally filtered by status.")]
    public async Task<IReadOnlyCollection<Memory>> ListAsync(
        [Description("Filter by status: active, superseded, archived, or 'all'. Defaults to 'active'.")] string status = "active",
        [Description("Scope: project, global, or merged. Defaults to 'merged'.")] string scope = "merged")
    {
        if (HasScoped)
        {
            var normalizedStatus = status;
            MemoryStatus? filter = null;
            if (!string.IsNullOrWhiteSpace(normalizedStatus) && !normalizedStatus.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<MemoryStatus>(normalizedStatus, ignoreCase: true, out var parsed))
                {
                    _logger?.LogWarning("memory_list failed: invalid status '{Status}'", status);
                    throw new ArgumentException($"Invalid memory status '{status}'. Valid statuses: {string.Join(", ", Enum.GetNames<MemoryStatus>())}, all", nameof(status));
                }
                filter = parsed;
            }

            var scoped = await _scoped!.ListAsync(scope, filter);
            var memories = scoped.Select(s => s.Memory).ToList().AsReadOnly();
            _logger?.LogInformation("Listed {Count} memories scope '{Scope}' status '{Status}'", memories.Count, scope, status);
            return memories;
        }

        var all = await _memory.ListAllAsync();
        if (string.IsNullOrWhiteSpace(status) || status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation("Listed all {Count} memories", all.Count);
            return all;
        }

        if (!Enum.TryParse<MemoryStatus>(status, ignoreCase: true, out var memoryStatus))
        {
            _logger?.LogWarning("memory_list failed: invalid status '{Status}'", status);
            throw new ArgumentException($"Invalid memory status '{status}'. Valid statuses: {string.Join(", ", Enum.GetNames<MemoryStatus>())}, all", nameof(status));
        }

        var filtered = all.Where(m => m.Status == memoryStatus).ToList().AsReadOnly();
        _logger?.LogInformation("Listed {Count} memories filtered by status '{Status}'", filtered.Count, memoryStatus);
        return filtered;
    }

    [McpServerTool(Name = "memory_search"), Description("Search memories by keyword query.")]
    public async Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(
        [Description("The search query")] string query,
        [Description("Maximum number of results to return. Defaults to 10.")] int limit = 10,
        [Description("Scope: project, global, or merged. Defaults to 'merged' (project + global with project priority).")] string scope = "merged")
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger?.LogWarning("memory_search failed: query is empty");
            throw new ArgumentException("Query cannot be empty.", nameof(query));
        }

        if (HasScoped)
        {
            var scopedResults = await _scoped!.SearchAsync(query, scope, limit);
            var results = scopedResults.Select(r => new MemorySearchResult(r.Id, r.Rank)).ToList().AsReadOnly();
            _logger?.LogInformation("Search for '{Query}' scope '{Scope}' returned {Count} results", query, scope, results.Count);
            return results;
        }

        var fallback = await _memory.SearchAsync(query);
        if (limit > 0 && fallback.Count > limit)
        {
            fallback = fallback.Take(limit).ToList().AsReadOnly();
        }

        _logger?.LogInformation("Search for '{Query}' returned {Count} results", query, fallback.Count);
        return fallback;
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

    [McpServerTool(Name = "memory_copy_to_project"), Description("Copy a global memory to the current project. Source remains unchanged.")]
    public async Task<Memory?> CopyToProjectAsync(
        [Description("The ULID of the memory to copy")] string id,
        [Description("Source scope: global or project. Defaults to 'global'.")] string sourceScope = "global")
    {
        if (!HasScoped) throw new InvalidOperationException("Scoped memory service not available");
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id cannot be empty.", nameof(id));
        var memoryId = MemoryId.Parse(id);
        var sourceKind = sourceScope.Trim().ToLowerInvariant() == "project" ? MemoryScopeKind.Project : MemoryScopeKind.Global;
        var scoped = _scoped ?? throw new InvalidOperationException("Scoped memory service not available");
        var source = new MemoryReference(memoryId, sourceKind, sourceKind == MemoryScopeKind.Project ? scoped.ProjectRoot : null);
        var copied = await scoped.CopyToProjectAsync(source, scoped.ProjectRoot!);
        return copied?.Memory;
    }

    [McpServerTool(Name = "memory_promote_to_global"), Description("Promote a project memory to global. Source remains unchanged.")]
    public async Task<Memory?> PromoteToGlobalAsync(
        [Description("The ULID of the memory to promote")] string id)
    {
        if (!HasScoped) throw new InvalidOperationException("Scoped memory service not available");
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id cannot be empty.", nameof(id));
        var memoryId = MemoryId.Parse(id);
        var scoped = _scoped ?? throw new InvalidOperationException("Scoped memory service not available");
        var source = new MemoryReference(memoryId, MemoryScopeKind.Project, scoped.ProjectRoot);
        var promoted = await scoped.PromoteToGlobalAsync(source);
        return promoted?.Memory;
    }
}
