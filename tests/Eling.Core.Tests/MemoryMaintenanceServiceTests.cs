using Eling.Core;
using Xunit;

namespace Eling.Core.Tests;

public class MemoryMaintenanceServiceTests
{
    [Fact]
    public async Task RunAsync_DryRun_DetectsFuzzyDuplicatesWithoutMutating()
    {
        var (service, storage, _) = NewServiceBypassSmartSave();
        await storage.SaveAsync(new Memory(MemoryType.Preference, "Always check git status before commit", new[] { "git" }));
        await storage.SaveAsync(new Memory(MemoryType.Preference, "Always check git status and diff before commit", new[] { "git" }));

        var maintenance = new MemoryMaintenanceService(service, new InMemoryMemoryIndex());
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
        var (service, storage, _) = NewServiceBypassSmartSave();
        var first = new Memory(MemoryType.Preference, "Always check git status before commit", new[] { "git" });
        var second = new Memory(MemoryType.Preference, "Always check git status and diff before commit", new[] { "git" });
        await storage.SaveAsync(first);
        await storage.SaveAsync(second);

        var maintenance = new MemoryMaintenanceService(service, new InMemoryMemoryIndex());

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
        var (service, storage, _) = NewServiceBypassSmartSave();
        await storage.SaveAsync(new Memory(MemoryType.Note, "   ", Array.Empty<string>()));
        var stale = new Memory(MemoryType.Note, "old", new[] { "x" }, status: MemoryStatus.Archived, createdAt: DateTimeOffset.UtcNow.AddDays(-100), updatedAt: DateTimeOffset.UtcNow.AddDays(-100));
        await storage.SaveAsync(stale);

        var maintenance = new MemoryMaintenanceService(service, new InMemoryMemoryIndex());
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
        var (service, storage, _) = NewServiceBypassSmartSave();
        await storage.SaveAsync(new Memory(MemoryType.Preference, "Use question tool before any task", new[] { "rule" }));
        await storage.SaveAsync(new Memory(MemoryType.Preference, "  Use question tool before any task  ", new[] { "rule" }));
        await storage.SaveAsync(new Memory(MemoryType.Preference, "Different content here", new[] { "rule" }));

        var maintenance = new MemoryMaintenanceService(service, new InMemoryMemoryIndex());
        var report = await maintenance.RunAsync(new MaintenanceRequest
        {
            Scope = MaintenanceScope.Project,
            DryRun = true
        });

        var dedup = report.Findings.Where(f => f.Operation == MaintenanceOperation.Dedup).ToList();
        Assert.Single(dedup);
    }

    private static (MemoryService Service, InMemoryMemoryStorage Storage, InMemoryMemoryIndex Index) NewServiceBypassSmartSave()
    {
        var storage = new InMemoryMemoryStorage();
        var index = new InMemoryMemoryIndex();
        var service = new MemoryService(storage, index, new SmartSaveOptions { EnableFuzzyMatch = false });
        return (service, storage, index);
    }
}
