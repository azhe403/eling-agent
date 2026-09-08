using System.Globalization;

namespace Eling.Core;
public class MemoryMaintenanceService : IMemoryMaintenanceService
{
    private readonly IMemoryService _service;
    private readonly IMemoryIndex _index;

    public MemoryMaintenanceService(IMemoryService service, IMemoryIndex index)
    {
        _service = service;
        _index = index;
    }

    public async Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SimilarityThreshold < 0 || request.SimilarityThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "similarityThreshold must be in [0, 1].");
        }
        if (request.StaleDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "staleDays must be >= 0.");
        }

        var scopes = request.Scope == MaintenanceScope.Merged
            ? new[] { MaintenanceScope.Project, MaintenanceScope.Global }
            : new[] { request.Scope };

        var allFindings = new List<MaintenanceFinding>();
        var stats = new MaintenanceStats();

        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scoped = await _service.ListAllAsync();
            stats.MemoriesScanned += scoped.Count;
            var scopeFindings = await RunForScopeAsync(scope, scoped, request, stats, cancellationToken);
            allFindings.AddRange(scopeFindings);
        }

        if (!request.DryRun)
        {
            await ApplyApprovedAsync(allFindings, request, stats, cancellationToken);
        }

        return new MaintenanceReport
        {
            DryRun = request.DryRun,
            Findings = allFindings,
            Stats = stats
        };
    }

    private async Task<List<MaintenanceFinding>> RunForScopeAsync(
        MaintenanceScope scope,
        IReadOnlyCollection<Memory> memories,
        MaintenanceRequest request,
        MaintenanceStats stats,
        CancellationToken cancellationToken)
    {
        var findings = new List<MaintenanceFinding>();
        var dedupedMemoryIds = new HashSet<MemoryId>();

        if (request.Operations.Contains(MaintenanceOperation.Dedup))
        {
            var dedupFindings = DetectExactDuplicates(scope, memories);
            foreach (var finding in dedupFindings)
            {
                foreach (var idValue in finding.MemoryIds)
                {
                    dedupedMemoryIds.Add(MemoryId.Parse(idValue));
                }
            }
            stats.DuplicateGroups += dedupFindings.Count;
            findings.AddRange(dedupFindings);
        }

        if (request.Operations.Contains(MaintenanceOperation.Merge))
        {
            var mergeCandidates = memories
                .Where(m => m.Status == MemoryStatus.Active && !dedupedMemoryIds.Contains(m.Id))
                .ToList();
            var mergeFindings = DetectFuzzyMergeGroups(scope, mergeCandidates, request.SimilarityThreshold);
            stats.MergeGroups += mergeFindings.Count;
            findings.AddRange(mergeFindings);
        }

        if (request.Operations.Contains(MaintenanceOperation.Cleanup))
        {
            var cleanupFindings = DetectCleanupCandidates(scope, memories, request.StaleDays);
            stats.CleanupCandidates += cleanupFindings.Count;
            findings.AddRange(cleanupFindings);
        }

        if (request.Operations.Contains(MaintenanceOperation.Reconcile))
        {
            var reconcileFindings = DetectReconcileCandidates(scope);
            stats.OrphanFiles += reconcileFindings.Count(f => f.ProposedAction == MaintenanceProposedAction.Index);
            stats.DanglingEntries += reconcileFindings.Count(f => f.ProposedAction == MaintenanceProposedAction.RemoveFromIndex);
            findings.AddRange(reconcileFindings);
        }

        return findings;
    }

    private static List<MaintenanceFinding> DetectExactDuplicates(MaintenanceScope scope, IReadOnlyCollection<Memory> memories)
    {
        var findings = new List<MaintenanceFinding>();
        var groups = memories
            .Where(m => m.Status == MemoryStatus.Active)
            .GroupBy(m => (m.Type, Normalize(m.Content)))
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var ids = group.Select(m => m.Id.Value).OrderBy(v => v, StringComparer.Ordinal).ToList();
            findings.Add(new MaintenanceFinding
            {
                Key = $"dedup:{scope.ToString().ToLowerInvariant()}:{string.Join(",", ids)}",
                Operation = MaintenanceOperation.Dedup,
                Scope = scope,
                MemoryIds = ids,
                ProposedAction = MaintenanceProposedAction.Merge,
                Reason = "identical normalized content"
            });
        }

        return findings;
    }

    private static List<MaintenanceFinding> DetectFuzzyMergeGroups(
        MaintenanceScope scope,
        IReadOnlyCollection<Memory> memories,
        double threshold)
    {
        var findings = new List<MaintenanceFinding>();
        var active = memories
            .Where(m => m.Status == MemoryStatus.Active)
            .ToList();

        var parent = new Dictionary<MemoryId, MemoryId>(active.Count);
        foreach (var m in active)
        {
            parent[m.Id] = m.Id;
        }

        MemoryId Find(MemoryId id)
        {
            var current = id;
            while (parent[current] != current)
            {
                parent[current] = parent[parent[current]];
                current = parent[current];
            }
            return current;
        }

        void Union(MemoryId a, MemoryId b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA != rootB)
            {
                parent[rootA] = rootB;
            }
        }

        for (var i = 0; i < active.Count; i++)
        {
            for (var j = i + 1; j < active.Count; j++)
            {
                var a = active[i];
                var b = active[j];
                if (a.Type != b.Type)
                {
                    continue;
                }
                var score = MemorySimilarity.CalculateJaccard(a.Content, b.Content);
                if (score >= threshold)
                {
                    Union(a.Id, b.Id);
                }
            }
        }

        var groups = active
            .GroupBy(m => Find(m.Id))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Value, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var ids = group.Select(m => m.Id.Value).OrderBy(v => v, StringComparer.Ordinal).ToList();
            findings.Add(new MaintenanceFinding
            {
                Key = $"merge:{scope.ToString().ToLowerInvariant()}:{string.Join(",", ids)}",
                Operation = MaintenanceOperation.Merge,
                Scope = scope,
                MemoryIds = ids,
                ProposedAction = MaintenanceProposedAction.Merge,
                Reason = $"fuzzy group (jaccard >= threshold)"
            });
        }

        return findings;
    }

    private static List<MaintenanceFinding> DetectCleanupCandidates(
        MaintenanceScope scope,
        IReadOnlyCollection<Memory> memories,
        int staleDays)
    {
        var findings = new List<MaintenanceFinding>();
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(staleDays);

        foreach (var memory in memories)
        {
            if (string.IsNullOrWhiteSpace(memory.Content))
            {
                findings.Add(new MaintenanceFinding
                {
                    Key = $"cleanup:{scope.ToString().ToLowerInvariant()}:{memory.Id.Value}:empty",
                    Operation = MaintenanceOperation.Cleanup,
                    Scope = scope,
                    MemoryIds = new[] { memory.Id.Value },
                    ProposedAction = MaintenanceProposedAction.Delete,
                    Reason = "empty content"
                });
                continue;
            }

            if (memory.Status is MemoryStatus.Archived or MemoryStatus.Superseded &&
                memory.UpdatedAt < cutoff)
            {
                findings.Add(new MaintenanceFinding
                {
                    Key = $"cleanup:{scope.ToString().ToLowerInvariant()}:{memory.Id.Value}:stale",
                    Operation = MaintenanceOperation.Cleanup,
                    Scope = scope,
                    MemoryIds = new[] { memory.Id.Value },
                    ProposedAction = MaintenanceProposedAction.Delete,
                    Reason = $"{memory.Status.ToString().ToLowerInvariant()} for >{staleDays}d"
                });
            }
        }

        return findings;
    }

    private static List<MaintenanceFinding> DetectReconcileCandidates(MaintenanceScope scope)
    {
        return new List<MaintenanceFinding>();
    }

    private static string Normalize(string content) => content.Trim().ToLowerInvariant();

    private async Task ApplyApprovedAsync(
        List<MaintenanceFinding> findings,
        MaintenanceRequest request,
        MaintenanceStats stats,
        CancellationToken cancellationToken)
    {
        foreach (var finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var autoApply = finding.Operation is MaintenanceOperation.Dedup or MaintenanceOperation.Reconcile;
                var approved = autoApply || request.ApproveFindingIds.Contains(finding.Key);
                if (!approved)
                {
                    finding.Result = "skipped: requires explicit approval";
                    stats.Skipped++;
                    continue;
                }

                switch (finding.ProposedAction)
                {
                    case MaintenanceProposedAction.Merge:
                        await ApplyMergeAsync(finding, stats, cancellationToken);
                        break;
                    case MaintenanceProposedAction.Delete:
                        await ApplyDeleteAsync(finding, stats, cancellationToken);
                        break;
                    case MaintenanceProposedAction.Index:
                    case MaintenanceProposedAction.RemoveFromIndex:
                        finding.Result = "noop: index reconcile not yet implemented";
                        stats.Skipped++;
                        continue;
                }

                finding.Applied = true;
            }
            catch (Exception ex)
            {
                finding.Result = $"failed: {ex.GetType().Name}: {ex.Message}";
                stats.Failed++;
            }
        }
    }

    private async Task ApplyMergeAsync(MaintenanceFinding finding, MaintenanceStats stats, CancellationToken cancellationToken)
    {
        var memories = new List<Memory>();
        foreach (var idValue in finding.MemoryIds)
        {
            var memory = await _service.GetByIdAsync(MemoryId.Parse(idValue));
            if (memory is null)
            {
                stats.Skipped++;
                finding.Result = $"skipped: missing {idValue}";
                return;
            }
            memories.Add(memory);
        }

        var survivor = memories.OrderBy(m => m.Id.Value, StringComparer.Ordinal).First();
        var absorbed = memories.Where(m => m.Id != survivor.Id).ToList();

        var mergedTags = memories
            .SelectMany(m => m.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? mostRecentSource = memories
            .Where(m => !string.IsNullOrWhiteSpace(m.Source))
            .OrderByDescending(m => m.UpdatedAt)
            .Select(m => m.Source)
            .FirstOrDefault();

        await _service.UpdateAsync(
            survivor.Id,
            content: survivor.Content,
            type: null,
            tags: mergedTags.ToArray(),
            source: mostRecentSource ?? survivor.Source,
            status: MemoryStatus.Active);
        stats.Merged++;

        foreach (var a in absorbed)
        {
            await _service.UpdateAsync(
                a.Id,
                content: null,
                type: null,
                tags: null,
                source: null,
                status: MemoryStatus.Superseded);
            stats.Superseded++;
        }

        finding.Result = "merged";
    }

    private async Task ApplyDeleteAsync(MaintenanceFinding finding, MaintenanceStats stats, CancellationToken cancellationToken)
    {
        foreach (var idValue in finding.MemoryIds)
        {
            await _service.DeleteAsync(MemoryId.Parse(idValue));
            stats.Deleted++;
        }
        finding.Result = "deleted";
    }
}
