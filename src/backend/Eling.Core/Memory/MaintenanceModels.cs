namespace Eling.Core;

public enum MaintenanceOperation
{
    Dedup,
    Merge,
    Cleanup,
    Reconcile
}

public enum MaintenanceProposedAction
{
    Merge,
    Delete,
    Index,
    RemoveFromIndex
}

public sealed class MaintenanceRequest
{
    public MaintenanceScope Scope { get; set; } = MaintenanceScope.Merged;
    public bool DryRun { get; set; } = true;
    public HashSet<MaintenanceOperation> Operations { get; set; } = new()
    {
        MaintenanceOperation.Dedup,
        MaintenanceOperation.Merge,
        MaintenanceOperation.Cleanup,
        MaintenanceOperation.Reconcile
    };
    public HashSet<string> ApproveFindingIds { get; set; } = new();
    public double SimilarityThreshold { get; set; } = 0.8;
    public int StaleDays { get; set; } = 90;
}

public enum MaintenanceScope
{
    Project,
    Global,
    Merged
}

public sealed class MaintenanceFinding
{
    public required string Key { get; init; }
    public required MaintenanceOperation Operation { get; init; }
    public required MaintenanceScope Scope { get; init; }
    public required IReadOnlyList<string> MemoryIds { get; init; }
    public required MaintenanceProposedAction ProposedAction { get; init; }
    public required string Reason { get; init; }
    public bool Applied { get; set; }
    public string? Result { get; set; }
}

public sealed class MaintenanceStats
{
    public int MemoriesScanned { get; set; }
    public int DuplicateGroups { get; set; }
    public int MergeGroups { get; set; }
    public int CleanupCandidates { get; set; }
    public int OrphanFiles { get; set; }
    public int DanglingEntries { get; set; }
    public int Merged { get; set; }
    public int Superseded { get; set; }
    public int Deleted { get; set; }
    public int Indexed { get; set; }
    public int RemovedFromIndex { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
}

public sealed class MaintenanceReport
{
    public required bool DryRun { get; init; }
    public required IReadOnlyList<MaintenanceFinding> Findings { get; init; }
    public required MaintenanceStats Stats { get; init; }
}
