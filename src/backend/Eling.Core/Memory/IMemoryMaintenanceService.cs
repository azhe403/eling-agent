namespace Eling.Core.Memory;

public interface IMemoryMaintenanceService
{
    Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken cancellationToken = default);
}
