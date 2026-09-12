using Eling.Core.Exceptions;
using Eling.Core.Memory.Storage;
using Eling.Core.Scope;

namespace Eling.Core.Memory;

public sealed class ScopedMemoryService : IScopedMemoryService
{
    private readonly IReadOnlyList<ProjectLevel> _levels;
    private readonly IMemoryService _globalService;
    private readonly IMemoryScopePolicy _policy;
    private readonly IMemoryMerger _merger;
    private readonly string _cwd;

    public IReadOnlyList<string> ChainRoots => _levels.Select(l => l.Scope.Root).ToList().AsReadOnly();

    public bool IsInitialized => _levels.Count > 0;

    public bool HasOwnScope =>
        _levels.Count > 0 &&
        string.Equals(
            _levels[0].Scope.Root.TrimEnd(Path.DirectorySeparatorChar),
            _cwd.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    public string Cwd => _cwd;

    public IMemoryService GlobalService => _globalService;

    public string? ProjectRoot => _levels.Count > 0 ? _levels[0].Scope.Root : null;

    public IMemoryService ProjectService =>
        _levels.Count > 0
            ? _levels[0].Service
            : throw new ProjectScopeNotInitializedException(_cwd);

    /// <summary>
    /// Resolves the logical name (last path segment) of an ancestor scope to its
    /// root. Opt-in: requires this workspace to have its own <c>.eling</c> scope,
    /// never creates a scope, and throws <see cref="InvalidProjectTargetException"/>
    /// when the name is unknown or no own scope exists.
    /// </summary>
    public string ResolveAncestorProjectRoot(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        var ancestorLevels = _levels.Skip(HasOwnScope ? 1 : 0).ToList();
        var available = ancestorLevels.Select(l => ProjectNameOf(l.Scope.Root)).ToList().AsReadOnly();

        if (!HasOwnScope)
        {
            throw new InvalidProjectTargetException(
                projectName,
                "this workspace has no own .eling scope; initialize one before targeting an ancestor",
                available);
        }

        var trimmed = projectName.Trim();
        var match = ancestorLevels.FirstOrDefault(l =>
            string.Equals(ProjectNameOf(l.Scope.Root), trimmed, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new InvalidProjectTargetException(
                projectName,
                "not a known ancestor scope of this workspace",
                available);
        }

        return match.Scope.Root;
    }

    /// <summary>
    /// Saves into a specific existing project level of the chain (used for
    /// explicit ancestor writes). The level must already be part of the chain, so
    /// no scope is ever created here.
    /// </summary>
    public async Task<ScopedSaveResult> SaveToProjectAsync(Memory memory, string targetProjectRoot)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectRoot);

        if (_levels.Count == 0)
        {
            throw new ProjectScopeNotInitializedException(_cwd);
        }

        var level = ResolveLevelByRoot(targetProjectRoot)
            ?? throw new InvalidProjectTargetException(
                ProjectNameOf(targetProjectRoot),
                "not a known project scope level of this workspace",
                _levels.Select(l => ProjectNameOf(l.Scope.Root)).ToList().AsReadOnly());

        var saveResult = await level.Service.SaveAsync(memory);
        var scoped = new ScopedMemory(saveResult.Memory, MemoryScopeKind.Project, level.Scope.Root);
        ScopedMemory? previous = saveResult.Previous is null
            ? null
            : new ScopedMemory(saveResult.Previous, MemoryScopeKind.Project, level.Scope.Root);
        return new ScopedSaveResult(scoped, saveResult.Action, previous);
    }

    /// <summary>Rebuilds the search index of a single project level of the chain.</summary>
    public async Task RebuildProjectIndexAsync(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var level = ResolveLevelByRoot(projectRoot);
        if (level is not null)
        {
            await level.Service.RebuildIndexAsync();
        }
    }

    private ProjectLevel? ResolveLevelByRoot(string projectRoot)
    {
        var target = projectRoot.TrimEnd(Path.DirectorySeparatorChar);
        return _levels.FirstOrDefault(l =>
            string.Equals(
                l.Scope.Root.TrimEnd(Path.DirectorySeparatorChar),
                target,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string ProjectNameOf(string root) =>
        Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>Old single-project ctor, kept as an N=1 chain wrapper.</summary>
    public ScopedMemoryService(
        IMemoryService projectService,
        IMemoryService globalService,
        IMemoryScopePolicy policy,
        IMemoryMerger merger,
        string? projectRoot)
        : this(
            [new ProjectLevel(new ProjectScope(projectRoot ?? Directory.GetCurrentDirectory()), projectService)],
            globalService,
            policy,
            merger,
            projectRoot ?? Directory.GetCurrentDirectory())
    {
    }

    public ScopedMemoryService(
        IReadOnlyList<ProjectLevel> levels,
        IMemoryService globalService,
        IMemoryScopePolicy policy,
        IMemoryMerger merger,
        string cwd)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(globalService);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(merger);
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        _levels = levels;
        _globalService = globalService;
        _policy = policy;
        _merger = merger;
        _cwd = Path.GetFullPath(cwd);
    }

    private IMemoryService ResolveService(MemoryScopeKind kind) =>
        kind == MemoryScopeKind.Project ? HeadOrThrow() : _globalService;

    private IMemoryService HeadOrThrow() =>
        _levels.Count > 0
            ? _levels[0].Service
            : throw new ProjectScopeNotInitializedException(_cwd);

    private IMemoryService ResolveProjectLevel(string? projectRoot)
    {
        if (_levels.Count == 0)
        {
            throw new ProjectScopeNotInitializedException(_cwd);
        }
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return _levels[0].Service;
        }
        var target = Path.GetFullPath(projectRoot);
        return _levels.FirstOrDefault(l =>
            string.Equals(
                l.Scope.Root.TrimEnd(Path.DirectorySeparatorChar),
                target.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))?.Service ?? _levels[0].Service;
    }

    public async Task<ScopedSaveResult> SaveAsync(Memory memory, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var kind = _policy.Resolve(scope);
        if (kind == MemoryScopeKind.Project && _levels.Count == 0)
        {
            throw new ProjectScopeNotInitializedException(_cwd);
        }
        var service = kind == MemoryScopeKind.Project ? _levels[0].Service : _globalService;
        var saveResult = await service.SaveAsync(memory);
        var root = kind == MemoryScopeKind.Project ? _levels[0].Scope.Root : null;
        var scoped = new ScopedMemory(saveResult.Memory, kind, root);
        ScopedMemory? previous = saveResult.Previous is null ? null : new ScopedMemory(saveResult.Previous, kind, root);
        return new ScopedSaveResult(scoped, saveResult.Action, previous);
    }

    public async Task<ScopedMemory?> GetByIdAsync(MemoryReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Scope == MemoryScopeKind.Project)
        {
            var service = ResolveProjectLevel(reference.ProjectRoot);
            var memory = await service.GetByIdAsync(reference.Id);
            return memory is null ? null : new ScopedMemory(memory, MemoryScopeKind.Project, reference.ProjectRoot ?? _levels[0].Scope.Root);
        }
        var global = await _globalService.GetByIdAsync(reference.Id);
        return global is null ? null : new ScopedMemory(global, MemoryScopeKind.Global, null);
    }

    public async Task<ScopedMemory?> GetByIdAsync(MemoryId id, string? scope)
    {
        var kind = _policy.Resolve(scope);
        if (kind == MemoryScopeKind.Project && _levels.Count == 0)
        {
            return null;
        }
        var service = kind == MemoryScopeKind.Project ? _levels[0].Service : _globalService;
        var memory = await service.GetByIdAsync(id);
        return memory is null ? null : new ScopedMemory(memory, kind, kind == MemoryScopeKind.Project ? _levels[0].Scope.Root : null);
    }

    public async Task<bool> DeleteAsync(MemoryReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Scope == MemoryScopeKind.Project)
        {
            var service = ResolveProjectLevel(reference.ProjectRoot);
            return await service.DeleteAsync(reference.Id);
        }
        return await _globalService.DeleteAsync(reference.Id);
    }

    public async Task<IReadOnlyCollection<ScopedMemory>> ListAsync(string? scope = null, MemoryStatus? status = null)
    {
        var normalized = string.IsNullOrWhiteSpace(scope) ? "merged" : scope.Trim().ToLowerInvariant();

        if (normalized == "project")
        {
            if (_levels.Count == 0)
            {
                return Array.Empty<ScopedMemory>();
            }
            var list = await _levels[0].Service.ListAllAsync();
            if (status.HasValue)
            {
                list = list.Where(m => m.Status == status.Value).ToList();
            }
            return list.Select(m => new ScopedMemory(m, MemoryScopeKind.Project, _levels[0].Scope.Root)).ToList().AsReadOnly();
        }

        if (normalized == "global")
        {
            var globals = await _globalService.ListAllAsync();
            if (status.HasValue)
            {
                globals = globals.Where(m => m.Status == status.Value).ToList();
            }
            return globals.Select(m => new ScopedMemory(m, MemoryScopeKind.Global, null)).ToList().AsReadOnly();
        }

        if (normalized is not ("merged" or "all"))
        {
            throw new ArgumentException($"Invalid scope '{scope}'. Valid: project, global, merged", nameof(scope));
        }

        var levels = new List<MemoryLevel>();
        foreach (var level in _levels)
        {
            var list = await level.Service.ListAllAsync();
            if (status.HasValue)
            {
                list = list.Where(m => m.Status == status.Value).ToList();
            }
            levels.Add(new MemoryLevel(level.Scope.Root, list));
        }
        var globalMemories = await _globalService.ListAllAsync();
        if (status.HasValue)
        {
            globalMemories = globalMemories.Where(m => m.Status == status.Value).ToList();
        }
        return _merger.MergeLists(levels, globalMemories);
    }

    public async Task<IReadOnlyCollection<ScopedSearchResult>> SearchAsync(string query, string? scope = null, int? limit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "merged" : scope.Trim().ToLowerInvariant();

        IReadOnlyCollection<ScopedSearchResult> merged;
        if (normalizedScope == "project")
        {
            if (_levels.Count == 0)
            {
                merged = Array.Empty<ScopedSearchResult>();
            }
            else
            {
                var results = await _levels[0].Service.SearchAsync(query);
                merged = results.Select(r => new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Project, _levels[0].Scope.Root, r.MatchedVia, r.PorterScore, r.TrigramScore, r.QueryMode)).ToList().AsReadOnly();
            }
        }
        else if (normalizedScope == "global")
        {
            var results = await _globalService.SearchAsync(query);
            merged = results.Select(r => new ScopedSearchResult(r.Id, r.Rank, MemoryScopeKind.Global, null, r.MatchedVia, r.PorterScore, r.TrigramScore, r.QueryMode)).ToList().AsReadOnly();
        }
        else if (normalizedScope is "merged" or "all")
        {
            var levels = new List<SearchResultLevel>();
            foreach (var level in _levels)
            {
                levels.Add(new SearchResultLevel(level.Scope.Root, await level.Service.SearchAsync(query)));
            }
            var globalResults = await _globalService.SearchAsync(query);
            merged = _merger.MergeSearchResults(levels, globalResults);
        }
        else
        {
            throw new ArgumentException($"Invalid scope '{scope}'. Valid: project, global, merged", nameof(scope));
        }

        if (limit.HasValue && limit.Value > 0 && merged.Count > limit.Value)
        {
            merged = ApplyInterleavedLimit(merged, limit.Value);
        }

        return merged;
    }

    // Round-robin truncation: plain Take(limit) would let a crowded head level
    // starve ancestor/global hits entirely. One slot per level per pass keeps
    // every level with results surfacing (>=1 guaranteed when the limit allows).
    private static IReadOnlyCollection<ScopedSearchResult> ApplyInterleavedLimit(
        IReadOnlyCollection<ScopedSearchResult> merged,
        int limit)
    {
        var groups = new List<List<ScopedSearchResult>>();
        var groupIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in merged)
        {
            var key = item.Scope == MemoryScopeKind.Global
                ? "\u0000global"
                : item.ProjectRoot ?? "\u0000project";
            if (!groupIndex.TryGetValue(key, out var index))
            {
                index = groups.Count;
                groupIndex[key] = index;
                groups.Add([]);
            }
            groups[index].Add(item);
        }

        var selected = new List<ScopedSearchResult>(limit);
        while (selected.Count < limit)
        {
            var addedThisPass = false;
            foreach (var group in groups)
            {
                if (selected.Count >= limit || group.Count == 0)
                {
                    continue;
                }
                selected.Add(group[0]);
                group.RemoveAt(0);
                addedThisPass = true;
            }
            if (!addedThisPass)
            {
                break;
            }
        }
        return selected.AsReadOnly();
    }

    public async Task<ScopedMemory?> UpdateAsync(MemoryReference reference, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Scope == MemoryScopeKind.Project)
        {
            var service = ResolveProjectLevel(reference.ProjectRoot);
            var updated = await service.UpdateAsync(reference.Id, content, type, tags, source, status);
            return updated is null ? null : new ScopedMemory(updated, MemoryScopeKind.Project, reference.ProjectRoot ?? _levels[0].Scope.Root);
        }
        var globalUpdated = await _globalService.UpdateAsync(reference.Id, content, type, tags, source, status);
        return globalUpdated is null ? null : new ScopedMemory(globalUpdated, MemoryScopeKind.Global, null);
    }

    public async Task RebuildIndexAsync(string? scope = null)
    {
        var normalized = string.IsNullOrWhiteSpace(scope) ? "merged" : scope.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "project":
                if (_levels.Count > 0)
                {
                    await _levels[0].Service.RebuildIndexAsync();
                }
                break;
            case "global":
                await _globalService.RebuildIndexAsync();
                break;
            case "merged":
            case "all":
                foreach (var level in _levels)
                {
                    await level.Service.RebuildIndexAsync();
                }
                await _globalService.RebuildIndexAsync();
                break;
            default:
                throw new ArgumentException($"Invalid scope '{scope}'. Valid: project, global, merged", nameof(scope));
        }
    }

    private (IMemoryService Service, bool IsCurrentHead) ResolveTargetService(string targetProjectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectRoot);
        if (_levels.Count == 0)
        {
            throw new ProjectScopeNotInitializedException(_cwd);
        }
        var target = Path.GetFullPath(targetProjectRoot);
        var headRoot = _levels[0].Scope.Root;
        if (string.Equals(headRoot.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return (_levels[0].Service, true);
        }
        var level = _levels.FirstOrDefault(l =>
            string.Equals(l.Scope.Root.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (level is not null)
        {
            return (level.Service, false);
        }
        var dataDir = target.EndsWith(ProjectScope.DataDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? target
            : Path.Combine(target, ProjectScope.DataDirectoryName);
        // Never auto-create a project scope via copy/move: only memory_init_project
        // may create .eling after user consent. SaveAsync would create the
        // directory implicitly, so refuse uninitialized targets here.
        if (!Directory.Exists(dataDir))
        {
            throw new ProjectScopeNotInitializedException(target);
        }
        return (new MemoryService(
            new FileSystemMemoryStorage(dataDir),
            new SqliteMemoryIndex(Path.Combine(dataDir, "index.db"))), false);
    }

    public async Task<ScopedMemory?> CopyToProjectAsync(MemoryReference source, string targetProjectRoot)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectRoot);

        IMemoryService sourceService = source.Scope == MemoryScopeKind.Project
            ? ResolveProjectLevel(source.ProjectRoot)
            : _globalService;
        var memory = await sourceService.GetByIdAsync(source.Id);
        if (memory is null) return null;

        var copy = new Memory(memory.Type, memory.Content, memory.Tags, memory.Source, memory.Status);
        var (targetService, _) = ResolveTargetService(targetProjectRoot);
        var saved = await targetService.SaveAsync(copy);
        return new ScopedMemory(saved, MemoryScopeKind.Project, targetProjectRoot);
    }

    public async Task<ScopedMemory?> CopyToGlobalAsync(MemoryReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Scope == MemoryScopeKind.Global)
        {
            throw new InvalidOperationException("Source is already Global");
        }

        var sourceService = ResolveProjectLevel(source.ProjectRoot);
        var memory = await sourceService.GetByIdAsync(source.Id);
        if (memory is null) return null;

        var copy = new Memory(memory.Type, memory.Content, memory.Tags, memory.Source, memory.Status);
        var saved = await _globalService.SaveAsync(copy);
        return new ScopedMemory(saved, MemoryScopeKind.Global, null);
    }

    public async Task<ScopedMemory?> PromoteToGlobalAsync(MemoryReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Scope == MemoryScopeKind.Global)
        {
            throw new InvalidOperationException("Source is already Global");
        }

        var sourceService = ResolveProjectLevel(source.ProjectRoot);
        var memory = await sourceService.GetByIdAsync(source.Id);
        if (memory is null) return null;

        var copy = new Memory(memory.Type, memory.Content, memory.Tags, memory.Source, memory.Status);
        var saved = await _globalService.SaveAsync(copy);
        return new ScopedMemory(saved, MemoryScopeKind.Global, null);
    }

    public async Task<ScopedMemory?> MoveToProjectAsync(MemoryReference source, string targetProjectRoot)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectRoot);
        if (source.Scope == MemoryScopeKind.Project && string.Equals(Path.GetFullPath(source.ProjectRoot ?? ""), Path.GetFullPath(targetProjectRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source and target project are the same");
        }

        IMemoryService sourceService = source.Scope == MemoryScopeKind.Project
            ? ResolveProjectLevel(source.ProjectRoot)
            : _globalService;
        var memory = await sourceService.GetByIdAsync(source.Id);
        if (memory is null) return null;

        var copy = new Memory(memory.Type, memory.Content, memory.Tags, memory.Source, memory.Status);
        var (targetService, _) = ResolveTargetService(targetProjectRoot);
        var saved = await targetService.SaveAsync(copy);
        await sourceService.DeleteAsync(source.Id);

        return new ScopedMemory(saved, MemoryScopeKind.Project, targetProjectRoot);
    }

    public async Task<ScopedMemory?> MoveToGlobalAsync(MemoryReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Scope == MemoryScopeKind.Global)
        {
            throw new InvalidOperationException("Source is already Global");
        }

        var sourceService = ResolveProjectLevel(source.ProjectRoot);
        var memory = await sourceService.GetByIdAsync(source.Id);
        if (memory is null) return null;

        var copy = new Memory(memory.Type, memory.Content, memory.Tags, memory.Source, memory.Status);
        var saved = await _globalService.SaveAsync(copy);
        await sourceService.DeleteAsync(source.Id);

        return new ScopedMemory(saved, MemoryScopeKind.Global, null);
    }
}
