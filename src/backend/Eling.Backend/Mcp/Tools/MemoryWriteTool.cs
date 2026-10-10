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

    [McpServerTool(Name = "memory_save"), Description("Save a memory to the knowledge store. Call this proactively whenever the turn contains something meant to outlive this conversation — either something the user states as durable (a standing rule or preference, a correction, a decision and its rationale, an explicit 'remember this') or a project/environment fact you observed while working that the tree does not already record. Do not wait for the words 'remember' or 'ingat', and never substitute a plain-text acknowledgement ('okay', 'noted', 'siap', 'sure') for the call: acknowledging without saving is a failure, and the acknowledgement and the save are additive. Skip transient task state, speculation, unverified claims, and anything already recorded in AGENTS.md, code, specs, or git history. The content is the main text to remember, and optional tags help with categorization. Provide 'project' to target an existing ancestor scope; this requires the current workspace to already have its own .eling scope.")]
    public async Task<SaveMemoryResponse> SaveAsync(
        [Description("The content to remember")] string content,
        [Description("Type of memory: fact, preference, decision, lesson, note. Defaults to 'fact'.")] string type = "fact",
        [Description("Optional tags for categorization")] string[]? tags = null,
        [Description("Optional source reference")] string? source = null,
        [Description("Scope: project, project-local, global, or auto. Defaults to 'project'. Decide the scope yourself from the content and never ask the user: durable shareable knowledge (decisions, conventions, rules, lessons) goes to 'project'; in-progress or temporary state goes to 'project-local' (machine-only, never committed); cross-project personal preferences go to 'global'.")] string scope = "project",
        [Description("Optional logical name of an ancestor project to target (e.g. the parent .eling). Requires this workspace to already have its own .eling scope; never creates a scope.")] string? project = null)
    {
        try
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
                    if (scope.Trim().Equals("project-local", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("Cannot target an ancestor project scope while scope is 'project-local'.", nameof(project));
                    }
                    var targetRoot = _scoped.ResolveAncestorProjectRoot(project);
                    var targetSaved = await _scoped.SaveToProjectAsync(memory, targetRoot);
                    _logger?.LogInformation("Saved memory '{Id}' with action '{Action}' to ancestor project '{Project}'", targetSaved.Id, targetSaved.Action, project);
                    await _notifier.NotifyAsync("mcp");
                    await _scoped.RebuildProjectIndexAsync(targetRoot);
                    return SaveMemoryResponse.From(targetSaved);
                }

                var wantsGlobal = string.Equals(scope?.Trim(), "global", StringComparison.OrdinalIgnoreCase);
                var projectScopeDisabled = false;
                if (!wantsGlobal && _policyStore is not null)
                {
                    var policy = await _policyStore.LoadAsync();
                    projectScopeDisabled = policy.Resolve(_scoped.Cwd) == ProjectScopeDecision.Disabled;
                }

                if (projectScopeDisabled && !wantsGlobal)
                {
                    // Declined init: project-bound saves land in the machine-only
                    // project-local shard — never global, never a shared scope.
                    var localSaved = await _scoped.SaveAsync(memory, "project-local");
                    _logger?.LogInformation("Saved memory '{Id}' with action '{Action}' to project-local (project scope disabled)", localSaved.Id, localSaved.Action);
                    await _notifier.NotifyAsync("mcp");
                    await _scoped.RebuildIndexAsync("project-local");
                    return SaveMemoryResponse.From(
                        localSaved,
                        projectScopeDisabled: true,
                        note: "Project scope is disabled for this workspace; saved to project-local (machine-only, never committed).");
                }

                try
                {
                    var scoped = await _scoped.SaveAsync(memory, scope);
                    _logger?.LogInformation("Saved memory '{Id}' with action '{Action}' scope '{Scope}' type '{Type}' ({Reason})", scoped.Id, scoped.Action, scoped.Memory.Type, scoped.Scope, scoped.Reason);
                    await _notifier.NotifyAsync("mcp");

                    if (scoped.Scope == MemoryScopeKind.Global)
                    {
                        await _scoped.RebuildIndexAsync("global");
                    }
                    else if (scoped.Scope == MemoryScopeKind.ProjectLocal)
                    {
                        await _scoped.RebuildIndexAsync("project-local");
                    }
                    else if (!string.IsNullOrWhiteSpace(scoped.ProjectRoot) && !string.Equals(scoped.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar), _scoped.Cwd.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        await _scoped.RebuildProjectIndexAsync(scoped.ProjectRoot);
                    }
                    else
                    {
                        await _scoped.RebuildIndexAsync("project");
                    }

                    return SaveMemoryResponse.From(scoped);
                }
                catch (ProjectScopeNotInitializedException ex)
                {
                    // Empty chain (no .eling anywhere up): save straight to the
                    // machine-only shard instead of blocking. Sharing via git
                    // still needs init — the note tells the agent to offer
                    // memory_init_project when the user wants that. Explicit
                    // ancestor targeting keeps blocking, as does an unwired
                    // local tier (InvalidOperationException below).
                    if (string.IsNullOrWhiteSpace(project) && _scoped is not null && _scoped.ChainRoots.Count == 0)
                    {
                        try
                        {
                            var localSaved = await _scoped.SaveAsync(memory, "project-local");
                            _logger?.LogInformation("Saved memory '{Id}' to project-local (no project scope initialized)", localSaved.Id);
                            await _notifier.NotifyAsync("mcp");
                            await _scoped.RebuildIndexAsync("project-local");
                            return SaveMemoryResponse.From(
                                localSaved,
                                note: "No project scope initialized; saved to project-local (machine-only, never committed). Offer memory_init_project if the user wants this shared via git.");
                        }
                        catch (Exception localEx) when (localEx is ProjectScopeNotInitializedException or InvalidOperationException)
                        {
                            // No local tier to fall back to — fall through to init-required.
                        }
                    }
                    _logger?.LogWarning("memory_save blocked: {Message}", ex.Message);
                    return SaveMemoryResponse.FromInitRequired(ex.Cwd);
                }
            }

            var saved = await _memory.SaveAsync(memory);
            _logger?.LogInformation("Saved memory '{Id}' with action '{Action}' type '{Type}' and {TagCount} tags ({Reason})", saved.Id, saved.Action, saved.Type, saved.Tags.Count, saved.Reason);
            await _notifier.NotifyAsync("mcp");
            await _memory.RebuildIndexAsync();
            return SaveMemoryResponse.From(saved, scope);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "memory_save threw an exception (scope={Scope}, type={Type})", scope, type);
            throw;
        }
    }

    [McpServerTool(Name = "memory_delete"), Description("Delete a memory by its ID. Returns true if the memory was deleted, false if it was not found. Provide 'project' to target an existing ancestor scope; this requires the current workspace to already have its own .eling scope.")]
    public async Task<bool> DeleteAsync(
        [Description("The ULID of the memory to delete")] string id,
        [Description("Scope: project, project-local, or global. Defaults to 'project'.")] string scope = "project",
        [Description("Optional logical name of an ancestor project to target (e.g. the parent .eling). Requires this workspace to already have its own .eling scope; never creates a scope.")] string? project = null)
    {
        try
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

                var normalizedDeleteScope = scope.Trim().ToLowerInvariant();
                var scopeKind = normalizedDeleteScope switch
                {
                    "global" => MemoryScopeKind.Global,
                    "project-local" => MemoryScopeKind.ProjectLocal,
                    _ => MemoryScopeKind.Project,
                };
                var reference = scopeKind switch
                {
                    MemoryScopeKind.Global => new MemoryReference(memoryId, scopeKind, null),
                    MemoryScopeKind.ProjectLocal => MemoryReference.ForProjectLocal(memoryId, _scoped.CanonicalRoot ?? _scoped.ProjectRoot ?? _scoped.Cwd),
                    _ => new MemoryReference(memoryId, scopeKind, _scoped.ProjectRoot),
                };
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "memory_delete threw an exception (id={Id}, scope={Scope})", id, scope);
            throw;
        }
    }
}

