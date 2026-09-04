using System.ComponentModel;
using Eling.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// MCP tools for the read-side of the memory store: get by id, list, search.
/// </summary>
[McpServerToolType]
public sealed class MemoryReadTool
{
    private readonly IMemoryService _memory;
    private readonly IScopedMemoryService? _scoped;
    private readonly ILogger<MemoryReadTool>? _logger;

    private bool HasScoped => _scoped is not null;

    public MemoryReadTool(IMemoryService memory, ILogger<MemoryReadTool>? logger = null)
    {
        _memory = memory;
        _logger = logger;
    }

    public MemoryReadTool(IScopedMemoryService scoped, ILogger<MemoryReadTool>? logger = null)
    {
        _scoped = scoped;
        _memory = scoped.ProjectService;
        _logger = logger;
    }

    public MemoryReadTool(IMemoryService memory, IScopedMemoryService scoped, ILogger<MemoryReadTool>? logger = null)
    {
        _memory = memory;
        _scoped = scoped;
        _logger = logger;
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
}
