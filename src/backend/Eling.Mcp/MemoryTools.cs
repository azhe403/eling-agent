using System.ComponentModel;
using Eling.Application;
using Eling.Core;
using Eling.Index;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Mcp;

[McpServerToolType]
public class MemoryTools
{
    private readonly IMemoryService _memory;
    private readonly ILogger<MemoryTools>? _logger;

    public MemoryTools(IMemoryService memory, ILogger<MemoryTools>? logger = null)
    {
        _memory = memory;
        _logger = logger;
    }

    [McpServerTool(Name = "memory_save"), Description("Save a memory to the knowledge store. The content is the main text to remember, and optional tags help with categorization.")]
    public async Task<Memory> SaveAsync(
        [Description("The content to remember")] string content,
        [Description("Type of memory: fact, preference, decision, lesson, note. Defaults to 'fact'.")] string type = "fact",
        [Description("Optional tags for categorization")] string[]? tags = null,
        [Description("Optional source reference")] string? source = null)
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
        [Description("New status: active, superseded, archived (omitted = unchanged)")] string? status = null)
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
        [Description("The ULID of the memory to retrieve")] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger?.LogWarning("memory_get failed: id is empty");
            throw new ArgumentException("Id cannot be empty.", nameof(id));
        }

        var memoryId = MemoryId.Parse(id);
        var result = await _memory.GetByIdAsync(memoryId);
        _logger?.LogInformation("Retrieved memory '{Id}' (found: {Found})", id, result is not null);
        return result;
    }

    [McpServerTool(Name = "memory_delete"), Description("Delete a memory by its ID. Returns true if the memory was deleted, false if it was not found.")]
    public async Task<bool> DeleteAsync(
        [Description("The ULID of the memory to delete")] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger?.LogWarning("memory_delete failed: id is empty");
            throw new ArgumentException("Id cannot be empty.", nameof(id));
        }

        var memoryId = MemoryId.Parse(id);
        var deleted = await _memory.DeleteAsync(memoryId);
        _logger?.LogInformation("Deleted memory '{Id}' (result: {Result})", id, deleted);
        return deleted;
    }

    [McpServerTool(Name = "memory_list"), Description("List memories, optionally filtered by status.")]
    public async Task<IReadOnlyCollection<Memory>> ListAsync(
        [Description("Filter by status: active, superseded, archived, or 'all'. Defaults to 'active'.")] string status = "active")
    {
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
        [Description("Maximum number of results to return. Defaults to 10.")] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger?.LogWarning("memory_search failed: query is empty");
            throw new ArgumentException("Query cannot be empty.", nameof(query));
        }

        var results = await _memory.SearchAsync(query);
        if (limit > 0 && results.Count > limit)
        {
            results = results.Take(limit).ToList().AsReadOnly();
        }

        _logger?.LogInformation("Search for '{Query}' returned {Count} results", query, results.Count);
        return results;
    }

    [McpServerTool(Name = "memory_rebuild_index"), Description("Rebuild the search index from all stored memories.")]
    public async Task RebuildIndexAsync()
    {
        _logger?.LogInformation("Rebuilding memory index");
        await _memory.RebuildIndexAsync();
        _logger?.LogInformation("Memory index rebuilt successfully");
    }
}
