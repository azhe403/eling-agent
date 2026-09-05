using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

internal static class PortMonitor
{
    public static async Task WaitForPortFreeAsync(
        int port,
        TimeSpan interval,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        const long LogEvery = 10;
        var ticks = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!DashboardPort.IsLoopbackListening(port))
            {
                logger.LogInformation(
                    "Dashboard port {Port} is free (poll #{Ticks}); peer will attempt promotion",
                    port, ticks);
                return;
            }

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            ticks++;
            logger.LogTrace(
                "Peer still polling port {Port} (poll #{Ticks}, interval {IntervalMs}ms)",
                port, ticks, (int)interval.TotalMilliseconds);
            if (ticks % LogEvery == 0)
            {
                logger.LogDebug(
                    "Peer still waiting on port {Port} (poll #{Ticks}, interval {IntervalMs}ms)",
                    port, ticks, (int)interval.TotalMilliseconds);
            }
        }
    }
}
