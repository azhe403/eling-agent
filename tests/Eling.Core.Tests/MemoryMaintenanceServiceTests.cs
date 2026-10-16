using Eling.Core;
using Eling.Core.Memory;
using Xunit;

namespace Eling.Core.Tests;

public class MemoryMaintenanceServiceTests
{
    [Fact]
    public async Task RunAsync_DryRun_DetectsFuzzyDuplicatesWithoutMutating()
    {
        var (maintenance, service, storage, _, _) = NewScopedMaintenance();
        await storage.SaveAsync(new Memory.Memory(MemoryType.Preference, "Always check git status before commit", new[] { "git" }));
        await storage.SaveAsync(new Memory.Memory(MemoryType.Preference, "Always check git status and diff before commit", new[] { "git" }));

        var report = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.Project,
            DryRun = true,
            SimilarityThreshold = 0.5
        });

        Assert.True(report.DryRun);
        Assert.Contains(report.Findings, f => f.Operation == MaintenanceOperation.Merge);
    }

    [Fact]
    public async Task RunAsync_ApplyMerge_MarksAbsorbedSuperseded()
    {
        var (maintenance, service, storage, _, _) = NewScopedMaintenance();
        var first = new Memory.Memory(MemoryType.Preference, "Always check git status before commit", new[] { "git" });
        var second = new Memory.Memory(MemoryType.Preference, "Always check git status and diff before commit", new[] { "git" });
        await storage.SaveAsync(first);
        await storage.SaveAsync(second);

        // Phase 1: dry-run to discover the merge finding key
        var dryRun = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.Project,
            DryRun = true,
            SimilarityThreshold = 0.5
        });
        var mergeFinding = Assert.Single(dryRun.Findings, f => f.Operation == MaintenanceOperation.Merge);
        Assert.False(mergeFinding.Applied);

        // Phase 2: apply with explicit approval
        var report = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.Project,
            DryRun = false,
            SimilarityThreshold = 0.5,
            ApproveFindingIds = new HashSet<string> { mergeFinding.Key }
        });

        var appliedFinding = Assert.Single(report.Findings, f => f.Operation == MaintenanceOperation.Merge);
        Assert.True(appliedFinding.Applied);
        Assert.Equal("merged", appliedFinding.Result);
        Assert.Equal(1, report.Stats.Merged);
        Assert.Equal(1, report.Stats.Superseded);

        var all = await service.ListAllAsync();
        var survivorId = string.CompareOrdinal(first.Id.Value, second.Id.Value) < 0 ? first.Id : second.Id;
        var absorbedId = survivorId == first.Id ? second.Id : first.Id;
        var absorbed = all.Single(m => m.Id == absorbedId);
        var survivor = all.Single(m => m.Id == survivorId);
        Assert.Equal(MemoryStatus.Superseded, absorbed.Status);
        Assert.Equal(MemoryStatus.Active, survivor.Status);
    }

    [Fact]
    public async Task RunAsync_DetectCleanup_EmptyAndStale()
    {
        var (maintenance, _, storage, _, _) = NewScopedMaintenance();
        await storage.SaveAsync(new Memory.Memory(MemoryType.Note, "   ", Array.Empty<string>()));
        var stale = new Memory.Memory(MemoryType.Note, "old", new[] { "x" }, status: MemoryStatus.Archived, createdAt: DateTimeOffset.UtcNow.AddDays(-100), updatedAt: DateTimeOffset.UtcNow.AddDays(-100));
        await storage.SaveAsync(stale);

        var report = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.Project,
            DryRun = true,
            StaleDays = 30
        });

        Assert.Contains(report.Findings, f => f.Operation == MaintenanceOperation.Cleanup && f.Reason == "empty content");
        Assert.Contains(report.Findings, f => f.Operation == MaintenanceOperation.Cleanup && f.Reason.Contains("archived"));
    }

    [Fact]
    public async Task RunAsync_DedupExactGroup_Detected()
    {
        var (maintenance, _, storage, _, _) = NewScopedMaintenance();
        await storage.SaveAsync(new Memory.Memory(MemoryType.Preference, "Use question tool before any task", new[] { "rule" }));
        await storage.SaveAsync(new Memory.Memory(MemoryType.Preference, "  Use question tool before any task  ", new[] { "rule" }));
        await storage.SaveAsync(new Memory.Memory(MemoryType.Preference, "Different content here", new[] { "rule" }));

        var report = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.Project,
            DryRun = true
        });

        var dedup = report.Findings.Where(f => f.Operation == MaintenanceOperation.Dedup).ToList();
        Assert.Single(dedup);
    }

    [Fact]
    public async Task RunAsync_Merged_ScansProjectLocalProjectAndGlobalSeparately()
    {
        var (maintenance, _, storage, globalStorage, localStorage) = NewScopedMaintenance();
        await storage.SaveAsync(new Memory.Memory(MemoryType.Preference, "Always check git status before commit", new[] { "git" }));
        await storage.SaveAsync(new Memory.Memory(MemoryType.Preference, "  always check git status before commit ", new[] { "git" }));
        await globalStorage.SaveAsync(new Memory.Memory(MemoryType.Preference, "always run tests before push", new[] { "ci" }));
        await globalStorage.SaveAsync(new Memory.Memory(MemoryType.Preference, "Always run tests before push", new[] { "ci" }));
        await localStorage.SaveAsync(new Memory.Memory(MemoryType.Fact, "scratch note for local shard", new[] { "ci" }));
        await localStorage.SaveAsync(new Memory.Memory(MemoryType.Fact, "scratch note for local SHARD", new[] { "ci" }));

        var report = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.Merged,
            DryRun = true
        });

        Assert.Equal(6, report.Stats.MemoriesScanned);
        var dedupScopes = report.Findings
            .Where(f => f.Operation == MaintenanceOperation.Dedup)
            .Select(f => f.Scope)
            .ToList();
        Assert.Contains(MaintenanceScope.ProjectLocal, dedupScopes);
        Assert.Contains(MaintenanceScope.Project, dedupScopes);
        Assert.Contains(MaintenanceScope.Global, dedupScopes);
        Assert.Equal(3, dedupScopes.Count);
    }

    [Fact]
    public async Task RunAsync_ScopeLocal_ListsOnlyTheLocalShard()
    {
        var (maintenance, _, storage, globalStorage, localStorage) = NewScopedMaintenance();
        await storage.SaveAsync(new Memory.Memory(MemoryType.Preference, "project only rule", new[] { "rule" }));
        await globalStorage.SaveAsync(new Memory.Memory(MemoryType.Preference, "global only rule", new[] { "rule" }));
        await localStorage.SaveAsync(new Memory.Memory(MemoryType.Fact, "always run tests before push", new[] { "ci" }));
        await localStorage.SaveAsync(new Memory.Memory(MemoryType.Fact, "Always run tests before PUSH", new[] { "ci" }));

        var report = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.ProjectLocal,
            DryRun = true
        });

        Assert.Equal(2, report.Stats.MemoriesScanned);
        var dedup = Assert.Single(report.Findings, f => f.Operation == MaintenanceOperation.Dedup);
        Assert.Equal(MaintenanceScope.ProjectLocal, dedup.Scope);
        Assert.StartsWith("dedup:projectlocal:", dedup.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ScopeProject_IgnoresGlobalAndLocal()
    {
        var (maintenance, _, storage, globalStorage, localStorage) = NewScopedMaintenance();
        await globalStorage.SaveAsync(new Memory.Memory(MemoryType.Preference, "global only rule", new[] { "rule" }));
        await localStorage.SaveAsync(new Memory.Memory(MemoryType.Fact, "local only note", new[] { "rule" }));

        var report = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.Project,
            DryRun = true
        });

        Assert.Equal(0, report.Stats.MemoriesScanned);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task RunAsync_ScopeGlobal_ListsOnlyGlobal()
    {
        var (maintenance, _, storage, globalStorage, _) = NewScopedMaintenance();
        await storage.SaveAsync(new Memory.Memory(MemoryType.Preference, "head project rule", new[] { "rule" }));
        await globalStorage.SaveAsync(new Memory.Memory(MemoryType.Preference, "always run tests before push", new[] { "ci" }));
        await globalStorage.SaveAsync(new Memory.Memory(MemoryType.Preference, "Always run tests before PUSH", new[] { "ci" }));

        var report = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.Global,
            DryRun = true
        });

        Assert.Equal(2, report.Stats.MemoriesScanned);
        var dedup = Assert.Single(report.Findings, f => f.Operation == MaintenanceOperation.Dedup);
        Assert.Equal(MaintenanceScope.Global, dedup.Scope);
    }

    private static (MemoryMaintenanceService Maintenance, MemoryService Service, InMemoryMemoryStorage Storage, InMemoryMemoryStorage GlobalStorage, InMemoryMemoryStorage LocalStorage) NewScopedMaintenance()
    {
        var storage = new InMemoryMemoryStorage();
        var index = new InMemoryMemoryIndex();
        var service = new MemoryService(storage, index, new SmartSaveOptions { EnableFuzzyMatch = false });
        var globalStorage = new InMemoryMemoryStorage();
        var globalService = new MemoryService(globalStorage, new InMemoryMemoryIndex(), new SmartSaveOptions { EnableFuzzyMatch = false });
        var localStorage = new InMemoryMemoryStorage();
        var localService = new MemoryService(localStorage, new InMemoryMemoryIndex(), new SmartSaveOptions { EnableFuzzyMatch = false });
        var scoped = new ScopedMemoryService(
            service,
            globalService,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            Directory.GetCurrentDirectory(),
            localService: localService);
        return (new MemoryMaintenanceService(scoped), service, storage, globalStorage, localStorage);
    }
}
