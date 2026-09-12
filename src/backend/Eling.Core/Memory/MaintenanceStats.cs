namespace Eling.Core.Memory;

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
