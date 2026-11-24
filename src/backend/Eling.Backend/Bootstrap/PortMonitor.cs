using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

internal static class PortMonitor
{
    private static readonly TimeSpan WarnAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(60);
    private static readonly int DefaultMaxPolls = 20;
    private static readonly TimeSpan DefaultInitialInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

    public static async Task WaitForPortFreeAsync(
        int port,
        TimeSpan interval,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        // Resolve safeguard limits from environment (for testing/ops overrides)
        var maxWait = ResolveMaxWait();
        var maxPolls = ResolveMaxPolls();
        var currentInterval = interval;

        const long LogEvery = 10;
        var ticks = 0L;
        var started = Stopwatch.GetTimestamp();
        logger.LogInformation(
            "Peer waiting on port {Port} (owner holds it; poll every {IntervalMs}ms, max wait {MaxWaitMs}ms, max polls {MaxPolls})",
            port, (int)interval.TotalMilliseconds, (int)maxWait.TotalMilliseconds, maxPolls);
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
                await Task.Delay(currentInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            ticks++;
            var elapsed = Stopwatch.GetElapsedTime(started);

            // Exponential backoff: double the interval each poll, capped at MaxInterval
            currentInterval = TimeSpan.FromMilliseconds(Math.Min(currentInterval.TotalMilliseconds * 2, MaxInterval.TotalMilliseconds));

            logger.LogTrace(
                "Peer still polling port {Port} (poll #{Ticks}, interval {IntervalMs}ms, waited {ElapsedMs}ms)",
                port, ticks, (int)currentInterval.TotalMilliseconds, (long)elapsed.TotalMilliseconds);

            // Check safeguard limits
            if (elapsed >= maxWait || ticks >= maxPolls)
            {
                logger.LogError(
                    "Peer promotion timeout on port {Port} after {ElapsedMs}ms ({Ticks} polls, max wait {MaxWaitMs}ms, max polls {MaxPolls}); giving up",
                    port, (long)elapsed.TotalMilliseconds, ticks, (int)maxWait.TotalMilliseconds, maxPolls);
                throw new TimeoutException(
                    $"Peer promotion timed out on port {port} after {elapsed.TotalMilliseconds:F0}ms ({ticks} polls). " +
                    $"Max wait: {maxWait.TotalMilliseconds}ms, Max polls: {maxPolls}.");
            }

            if (ticks % LogEvery == 0)
            {
                if (elapsed >= WarnAfter)
                {
                    logger.LogWarning(
                        "Peer still waiting on port {Port} (poll #{Ticks}, interval {IntervalMs}ms, waited {ElapsedMs}ms); tools/call stays queued until promotion",
                        port, ticks, (int)currentInterval.TotalMilliseconds, (long)elapsed.TotalMilliseconds);
                }
                else
                {
                    logger.LogDebug(
                        "Peer still waiting on port {Port} (poll #{Ticks}, interval {IntervalMs}ms, waited {ElapsedMs}ms)",
                        port, ticks, (int)currentInterval.TotalMilliseconds, (long)elapsed.TotalMilliseconds);
                }
            }
        }
    }

    private static TimeSpan ResolveMaxWait()
    {
        var raw = Environment.GetEnvironmentVariable("ELING_MAX_WAIT_MS");
        if (int.TryParse(raw, out var ms) && ms > 0) return TimeSpan.FromMilliseconds(ms);
        return DefaultMaxWait;
    }

    private static int ResolveMaxPolls()
    {
        var raw = Environment.GetEnvironmentVariable("ELING_MAX_POLLS");
        if (int.TryParse(raw, out var polls) && polls > 0) return polls;
        return DefaultMaxPolls;
    }

    private static long ElapsedMs(long started)
        => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
