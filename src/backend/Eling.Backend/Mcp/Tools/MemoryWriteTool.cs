using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Backend.Scope;
using Eling.Core;
using Eling.Core.Exceptions;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// MCP tools for the write-side of the memory store: create, delete.
/// </summary>
[McpServerToolType]
public sealed class MemoryWriteTool
{
    private readonly IMemoryService _memory;
    private readonly IScopedMemoryService? _scoped;
    private readonly IMemoryChangeNotifier _notifier;
    private readonly ILogger<MemoryWriteTool>? _logger;
    private readonly IProjectScopePolicyStore? _policyStore;

    private bool HasScoped => _scoped is not null;

    public MemoryWriteTool(IMemoryService memory, ILogger<MemoryWriteTool>? logger = null, IMemoryChangeNotifier? notifier = null)
    {
        _memory = memory;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
    }

    public MemoryWriteTool(IScopedMemoryService scoped, ILogger<MemoryWriteTool>? logger = null, IMemoryChangeNotifier? notifier = null, IProjectScopePolicyStore? policyStore = null)
    {
        _scoped = scoped;
        _memory = scoped.ProjectService;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
        _policyStore = policyStore;
    }

    public MemoryWriteTool(IMemoryService memory, IScopedMemoryService scoped, ILogger<MemoryWriteTool>? logger = null, IMemoryChangeNotifier? notifier = null, IProjectScopePolicyStore? policyStore = null)
    {
        _memory = memory;
        _scoped = scoped;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
        _policyStore = policyStore;
    }

    [McpServerTool(Name = "memory_save"), Description("Save a memory to the knowledge store. The content is the main text to remember, and optional tags help with categorization. Provide 'project' to target an existing ancestor scope; this requires the current workspace to already have its own .eling scope.")]
    public async Task<SaveMemoryResponse> SaveAsync(
        [Description("The content to remember")] string content,
        [Description("Type of memory: fact, preference, decision, lesson, note. Defaults to 'fact'.")] string type = "fact",
        [Description("Optional tags for categorization")] string[]? tags = null,
        [Description("Optional source reference")] string? source = null,
        [Description("Scope: project, global, or auto. Defaults to 'project'.")] string scope = "project",
        [Description("Optional logical name of an ancestor project to target (e.g. the parent .eling). Requires this workspace to have its own .eling scope; never creates a scope.")] string? project = null)
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
            if (!string.IsNullOrWhiteSpace(project))
            {
                if (scope.Trim().Equals("global", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Cannot target a project scope while scope is 'global'.", nameof(project));
                }
                var targetRoot = _scoped.ResolveAncestorProjectRoot(project);
                var targetSaved = await _scoped.SaveToProjectAsync(memory, targetRoot);
                _logger?.LogInformation("Saved memory '{Id}' with action '{Action}' to ancestor project '{Project}'", targetSaved.Id, targetSaved.Action, project);
                await _notifier.NotifyAsync("mcp");
                await _scoped.RebuildProjectIndexAsync(targetRoot);
                return SaveMemoryResponse.From(targetSaved);
            }

            // A workspace whose policy is `disabled` behaves as global-only: a
            // project-targeted save is rerouted to global (explicitly flagged)
            // instead of blocking with init-required, which would re-trigger the
            // onboarding prompt the policy is meant to silence.
            var wantsGlobal = string.Equals(scope?.Trim(), "global", StringComparison.OrdinalIgnoreCase);
            var projectScopeDisabled = false;
            if (!wantsGlobal && _policyStore is not null)
            {
                var policy = await _policyStore.LoadAsync();
                projectScopeDisabled = policy.Resolve(_scoped.Cwd) == ProjectScopeDecision.Disabled;
            }

            var effectiveScope = projectScopeDisabled ? "global" : scope;

            try
            {
                var scoped = await _scoped.SaveAsync(memory, effectiveScope);
                _logger?.LogInformation("Saved memory '{Id}' with action '{Action}' scope '{Scope}' type '{Type}'", scoped.Id, scoped.Action, scoped.Memory.Type, scoped.Scope);
                await _notifier.NotifyAsync("mcp");
                await _scoped.RebuildIndexAsync(effectiveScope);
                return SaveMemoryResponse.From(
                    scoped,
                    projectScopeDisabled,
                    projectScopeDisabled ? "Project scope is disabled for this workspace; saved to global." : null);
            }
            catch (ProjectScopeNotInitializedException ex)
            {
                _logger?.LogWarning("memory_save blocked: {Message}", ex.Message);
                return SaveMemoryResponse.FromInitRequired(ex.Cwd);
            }
        }

        var saved = await _memory.SaveAsync(memory);
        _logger?.LogInformation("Saved memory '{Id}' with action '{Action}' type '{Type}' and {TagCount} tags", saved.Id, saved.Action, saved.Type, saved.Tags.Count);
        await _notifier.NotifyAsync("mcp");
        await _memory.RebuildIndexAsync();
        return SaveMemoryResponse.From(saved, scope);
    }

    [McpServerTool(Name = "memory_delete"), Description("Delete a memory by its ID. Returns true if the memory was deleted, false if it was not found. Provide 'project' to target an existing ancestor scope; this requires the current workspace to already have its own .eling scope.")]
    public async Task<bool> DeleteAsync(
        [Description("The ULID of the memory to delete")] string id,
        [Description("Scope: project or global. Defaults to 'project'.")] string scope = "project",
        [Description("Optional logical name of an ancestor project to target (e.g. the parent .eling). Requires this workspace to have its own .eling scope; never creates a scope.")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger?.LogWarning("memory_delete failed: id is empty");
            throw new ArgumentException("Id cannot be empty.", nameof(id));
        }

        var memoryId = MemoryId.Parse(id);
        if (HasScoped && _scoped is not null)
        {
            if (!string.IsNullOrWhiteSpace(project))
            {
                var targetRoot = _scoped.ResolveAncestorProjectRoot(project);
                var targetReference = MemoryReference.ForProject(memoryId, targetRoot);
                var targetDeleted = await _scoped.DeleteAsync(targetReference);
                _logger?.LogInformation("Deleted memory '{Id}' from ancestor project '{Project}' (result: {Result})", id, project, targetDeleted);
                if (targetDeleted)
                {
                    await _notifier.NotifyAsync("mcp");
                    await _scoped.RebuildProjectIndexAsync(targetRoot);
                }
                return targetDeleted;
            }

            var scopeKind = scope.Trim().ToLowerInvariant() == "global" ? MemoryScopeKind.Global : MemoryScopeKind.Project;
            var reference = new MemoryReference(memoryId, scopeKind, scopeKind == MemoryScopeKind.Project ? _scoped.ProjectRoot : null);
            var deleted = await _scoped.DeleteAsync(reference);
            _logger?.LogInformation("Deleted memory '{Id}' scope '{Scope}' (result: {Result})", id, scopeKind, deleted);
            if (deleted)
            {
                await _notifier.NotifyAsync("mcp");
                await _scoped.RebuildIndexAsync(scope);
            }
            return deleted;
        }

        var result = await _memory.DeleteAsync(memoryId);
        _logger?.LogInformation("Deleted memory '{Id}' (result: {Result})", id, result);
        if (result)
        {
            await _notifier.NotifyAsync("mcp");
            await _memory.RebuildIndexAsync();
        }
        return result;
    }
}

