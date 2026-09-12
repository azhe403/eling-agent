namespace Eling.Core.Memory;

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
