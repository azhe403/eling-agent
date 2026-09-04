using Eling.Core;

namespace Eling.Core;

public sealed class ScopedMemoryService : IScopedMemoryService
{
    private readonly IMemoryService _projectService;
    private readonly IMemoryService _globalService;
    private readonly IMemoryScopePolicy _policy;
    private readonly IMemoryMerger _merger;
    private readonly string? _projectRoot;

    public IMemoryService ProjectService => _projectService;
    public IMemoryService GlobalService => _globalService;
    public string? ProjectRoot => _projectRoot;

    public ScopedMemoryService(
        IMemoryService projectService,
        IMemoryService globalService,
        IMemoryScopePolicy policy,
        IMemoryMerger merger,
        string? projectRoot)
    {
        _projectService = projectService;
        _globalService = globalService;
        _policy = policy;
        _merger = merger;
        _projectRoot = projectRoot;
    }

    private IMemoryService ResolveService(MemoryScopeKind kind) =>
        kind == MemoryScopeKind.Project ? _projectService : _globalService;

    public async Task<ScopedSaveResult> SaveAsync(Memory memory, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var kind = _policy.Resolve(scope);
        var service = ResolveService(kind);
        var saveResult = await service.SaveAsync(memory);
        var scoped = new ScopedMemory(saveResult.Memory, kind, kind == MemoryScopeKind.Project ? _projectRoot : null);
        return new ScopedSaveResult(scoped, saveResult.Action);
    }

    public async Task<ScopedMemory?> GetByIdAsync(MemoryReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var service = ResolveService(reference.Scope);
        var memory = await service.GetByIdAsync(reference.Id);
        return memory is null ? null : new ScopedMemory(memory, reference.Scope, reference.ProjectRoot);
    }

    public async Task<ScopedMemory?> GetByIdAsync(MemoryId id, string? scope)
    {
        var kind = _policy.Resolve(scope);
        var service = ResolveService(kind);
        var memory = await service.GetByIdAsync(id);
        return memory is null ? null : new ScopedMemory(memory, kind, kind == MemoryScopeKind.Project ? _projectRoot : null);
    }

    public async Task<bool> DeleteAsync(MemoryReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var service = ResolveService(reference.Scope);
        return await service.DeleteAsync(reference.Id);
    }

    public async Task<IReadOnlyCollection<ScopedMemory>> ListAsync(string? scope = null, MemoryStatus? status = null)
    {
        if (!string.IsNullOrWhiteSpace(scope))
        {
            var normalized = scope.Trim().ToLowerInvariant();
            if (normalized == "project" || normalized == "global")
            {
                var kind = _policy.Resolve(normalized);
                var service = ResolveService(kind);
                var list = await service.ListAllAsync();
                if (status.HasValue)
                    list = list.Where(m => m.Status == status.Value).ToList();
                return list.Select(m => new ScopedMemory(m, kind, kind == MemoryScopeKind.Project ? _projectRoot : null)).ToList().AsReadOnly();
            }
            if (normalized == "merged" || normalized == "all")
            {
                // fall through to merged
            }
            else
            {
                throw new ArgumentException($"Invalid scope '{scope}'. Valid: project, global, merged", nameof(scope));
            }
        }

        // Merged: project + global
        var projectMemories = await _projectService.ListAllAsync();
        var globalMemories = await _globalService.ListAllAsync();
        if (status.HasValue)
        {
            projectMemories = projectMemories.Where(m => m.Status == status.Value).ToList();
            globalMemories = globalMemories.Where(m => m.Status == status.Value).ToList();
        }
        return _merger.MergeLists(projectMemories, globalMemories, _projectRoot);
    }

    public async Task<IReadOnlyCollection<ScopedSearchResult>> SearchAsync(string query, string? scope = null, int? limit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "merged" : scope.Trim().ToLowerInvariant();

        IReadOnlyCollection<MemorySearchResult> projectResults = Array.Empty<MemorySearchResult>();
        IReadOnlyCollection<MemorySearchResult> globalResults = Array.Empty<MemorySearchResult>();

        switch (normalizedScope)
        {
            case "project":
                projectResults = await _projectService.SearchAsync(query);
                break;
            case "global":
                globalResults = await _globalService.SearchAsync(query);
                break;
            case "merged":
                projectResults = await _projectService.SearchAsync(query);
                globalResults = await _globalService.SearchAsync(query);
                break;
            default:
                throw new ArgumentException($"Invalid scope '{scope}'. Valid: project, global, merged", nameof(scope));
        }

        IReadOnlyCollection<ScopedSearchResult> merged;
        if (normalizedScope == "project")
        {
            merged = projectResults.Select(r => new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Project, _projectRoot)).ToList().AsReadOnly();
        }
        else if (normalizedScope == "global")
        {
            merged = globalResults.Select(r => new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Global, null)).ToList().AsReadOnly();
        }
        else
        {
            merged = _merger.MergeSearchResults(projectResults, globalResults, _projectRoot);
        }

        if (limit.HasValue && limit.Value > 0 && merged.Count > limit.Value)
        {
            merged = merged.Take(limit.Value).ToList().AsReadOnly();
        }

        return merged;
    }

    public async Task<ScopedMemory?> UpdateAsync(MemoryReference reference, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var service = ResolveService(reference.Scope);
        var updated = await service.UpdateAsync(reference.Id, content, type, tags, source, status);
        return updated is null ? null : new ScopedMemory(updated, reference.Scope, reference.ProjectRoot);
    }

    public async Task RebuildIndexAsync(string? scope = null)
    {
        var normalized = string.IsNullOrWhiteSpace(scope) ? "merged" : scope.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "project":
                await _projectService.RebuildIndexAsync();
                break;
            case "global":
                await _globalService.RebuildIndexAsync();
                break;
            case "merged":
            case "all":
                await _projectService.RebuildIndexAsync();
                await _globalService.RebuildIndexAsync();
                break;
            default:
                throw new ArgumentException($"Invalid scope '{scope}'. Valid: project, global, merged", nameof(scope));
        }
    }

    public async Task<ScopedMemory?> CopyToProjectAsync(MemoryReference source, string targetProjectRoot)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectRoot);

        var sourceService = ResolveService(source.Scope);
        var memory = await sourceService.GetByIdAsync(source.Id);
        if (memory is null) return null;

        // Create copy with new ID in target project scope
        var copy = new Memory(memory.Type, memory.Content, memory.Tags, memory.Source, memory.Status);
        // Use project service of this instance if target matches current project, otherwise create ephemeral service
        IMemoryService targetService;
        if (_projectRoot != null && string.Equals(Path.GetFullPath(targetProjectRoot), Path.GetFullPath(_projectRoot), StringComparison.OrdinalIgnoreCase))
        {
            targetService = _projectService;
        }
        else
        {
            var dataDir = Path.GetFullPath(targetProjectRoot).EndsWith(".eling", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(targetProjectRoot)
                : Path.Combine(Path.GetFullPath(targetProjectRoot), ".eling");
            targetService = new MemoryService(
                new FileSystemMemoryStorage(dataDir),
                new SqliteMemoryIndex(Path.Combine(dataDir, "index.db")));
            // Ensure .eling exists via storage
        }

        var saved = await targetService.SaveAsync(copy);
        return new ScopedMemory(saved, MemoryScopeKind.Project, targetProjectRoot);
    }

    public async Task<ScopedMemory?> PromoteToGlobalAsync(MemoryReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Scope == MemoryScopeKind.Global)
        {
            throw new InvalidOperationException("Source is already Global");
        }

        var sourceService = ResolveService(source.Scope);
        var memory = await sourceService.GetByIdAsync(source.Id);
        if (memory is null) return null;

        var copy = new Memory(memory.Type, memory.Content, memory.Tags, memory.Source, memory.Status);
        var saved = await _globalService.SaveAsync(copy);
        return new ScopedMemory(saved, MemoryScopeKind.Global, null);
    }
}

