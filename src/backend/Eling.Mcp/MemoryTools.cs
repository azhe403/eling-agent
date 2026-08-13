using System.ComponentModel;
using Eling.Application;
using Eling.Core;
using Eling.Index;
using ModelContextProtocol.Server;

namespace Eling.Mcp;

[McpServerToolType]
public class MemoryTools
{
    private readonly IMemoryService _memory;

    public MemoryTools(IMemoryService memory)
    {
        _memory = memory;
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
            throw new ArgumentException("Content cannot be empty.", nameof(content));
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            type = "fact";
        }

        if (!Enum.TryParse<MemoryType>(type, ignoreCase: true, out var memoryType))
        {
            throw new ArgumentException($"Invalid memory type '{type}'. Valid types: {string.Join(", ", Enum.GetNames<MemoryType>())}", nameof(type));
        }

        var memory = new Memory(memoryType, content, tags, source);
        return await _memory.SaveAsync(memory);
    }

    [McpServerTool(Name = "memory_get"), Description("Retrieve a memory by its ID.")]
    public async Task<Memory?> GetByIdAsync(
        [Description("The ULID of the memory to retrieve")] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id cannot be empty.", nameof(id));
        }

        var memoryId = MemoryId.Parse(id);
        return await _memory.GetByIdAsync(memoryId);
    }

    [McpServerTool(Name = "memory_delete"), Description("Delete a memory by its ID. Returns true if the memory was deleted, false if it was not found.")]
    public async Task<bool> DeleteAsync(
        [Description("The ULID of the memory to delete")] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id cannot be empty.", nameof(id));
        }

        var memoryId = MemoryId.Parse(id);
        return await _memory.DeleteAsync(memoryId);
    }

    [McpServerTool(Name = "memory_list"), Description("List memories, optionally filtered by status.")]
    public async Task<IReadOnlyCollection<Memory>> ListAsync(
        [Description("Filter by status: active, superseded, archived, or 'all'. Defaults to 'active'.")] string status = "active")
    {
        var all = await _memory.ListAllAsync();
        if (string.IsNullOrWhiteSpace(status) || status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return all;
        }

        if (!Enum.TryParse<MemoryStatus>(status, ignoreCase: true, out var memoryStatus))
        {
            throw new ArgumentException($"Invalid memory status '{status}'. Valid statuses: {string.Join(", ", Enum.GetNames<MemoryStatus>())}, all", nameof(status));
        }

        return all.Where(m => m.Status == memoryStatus).ToList().AsReadOnly();
    }

    [McpServerTool(Name = "memory_search"), Description("Search memories by keyword query.")]
    public async Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(
        [Description("The search query")] string query,
        [Description("Maximum number of results to return. Defaults to 10.")] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query cannot be empty.", nameof(query));
        }

        var results = await _memory.SearchAsync(query);
        if (limit > 0 && results.Count > limit)
        {
            return results.Take(limit).ToList().AsReadOnly();
        }

        return results;
    }

    [McpServerTool(Name = "memory_rebuild_index"), Description("Rebuild the search index from all stored memories.")]
    public async Task RebuildIndexAsync()
    {
        await _memory.RebuildIndexAsync();
    }
}
