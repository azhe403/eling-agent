namespace Eling.Core.Memory;

public sealed class MaintenanceReport
{
    public required bool DryRun { get; init; }
    public required IReadOnlyList<MaintenanceFinding> Findings { get; init; }
    public required MaintenanceStats Stats { get; init; }
}
