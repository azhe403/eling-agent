namespace Eling.Core;

public interface IMemoryMaintenanceService
{
    Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken cancellationToken = default);
}
