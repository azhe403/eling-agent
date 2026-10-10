using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Updates;

/// <summary>
/// Periodic update-check pump: one jittered check shortly after startup, then
/// on the check interval. Disabled entirely via <c>ELING_DISABLE_UPDATE_CHECK</c>.
/// Registered only on the dashboard owner, so MCP-only peers never fetch.
/// </summary>
public sealed class UpdateCheckerService : BackgroundService
{
    private static readonly TimeSpan StartupJitterMax = TimeSpan.FromMinutes(10);

    private readonly IUpdateChecker _checker;
    private readonly ILogger<UpdateCheckerService> _logger;

    public UpdateCheckerService(IUpdateChecker checker, ILogger<UpdateCheckerService> logger)
    {
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(logger);

        _checker = checker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (UpdateChecker.IsDisabled())
        {
            _logger.LogInformation("Update checks are disabled via ELING_DISABLE_UPDATE_CHECK");
            return;
        }

        await DelayAsync(JitteredStartupDelay(), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = await _checker.CheckNowAsync(stoppingToken);
                if (status.UpdateAvailable)
                {
                    _logger.LogInformation(
                        "Update available: {LatestVersion} (current {CurrentVersion})",
                        status.LatestVersion,
                        status.CurrentVersion);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled update check failed");
            }

            await DelayAsync(UpdateChecker.CheckInterval, stoppingToken);
        }
    }

    private static TimeSpan JitteredStartupDelay()
        => TimeSpan.FromMinutes(Random.Shared.NextDouble() * StartupJitterMax.TotalMinutes);

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
