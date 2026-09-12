namespace Eling.Core.Memory;

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
