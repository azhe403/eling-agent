using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

internal static class PortMonitor
{
    private static readonly TimeSpan WarnAfter = TimeSpan.FromMinutes(5);

    public static async Task WaitForPortFreeAsync(
        int port,
        TimeSpan interval,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        const long LogEvery = 10;
        var ticks = 0L;
        var started = Stopwatch.GetTimestamp();
        logger.LogInformation(
            "Peer waiting on port {Port} (owner holds it; poll every {IntervalMs}ms)",
            port, (int)interval.TotalMilliseconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!DashboardPort.IsLoopbackListening(port))
            {
                logger.LogInformation(
                    "Dashboard port {Port} is free after {ElapsedMs}ms (poll #{Ticks}); peer will attempt promotion",
                    port, ElapsedMs(started), ticks);
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
                "Peer still polling port {Port} (poll #{Ticks}, interval {IntervalMs}ms, waited {ElapsedMs}ms)",
                port, ticks, (int)interval.TotalMilliseconds, ElapsedMs(started));
            if (ticks % LogEvery == 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed >= WarnAfter)
                {
                    logger.LogWarning(
                        "Peer still waiting on port {Port} (poll #{Ticks}, interval {IntervalMs}ms, waited {ElapsedMs}ms); tools/call stays queued until promotion",
                        port, ticks, (int)interval.TotalMilliseconds, (long)elapsed.TotalMilliseconds);
                }
                else
                {
                    logger.LogDebug(
                        "Peer still waiting on port {Port} (poll #{Ticks}, interval {IntervalMs}ms, waited {ElapsedMs}ms)",
                        port, ticks, (int)interval.TotalMilliseconds, (long)elapsed.TotalMilliseconds);
                }
            }
        }
    }

    private static long ElapsedMs(long started)
        => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
