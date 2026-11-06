using Eling.Backend.Logging;
using Microsoft.Extensions.Hosting;

namespace Eling.Backend.Mcp;

public sealed class DailyLogRollerService : BackgroundService
{
    private readonly string _logsDirectory;
    private readonly LogFormatForwardingSink? _sink;

    /// <param name="sink">
    /// The DI-registered forwarder, not the inner sink: a format switch replaces
    /// the inner, and the rollover must follow whatever is current.
    /// </param>
    public DailyLogRollerService(string logsDirectory, LogFormatForwardingSink? sink = null)
    {
        _logsDirectory = logsDirectory;
        _sink = sink;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextMidnight = now.Date.AddDays(1).AddSeconds(1);
            var delay = nextMidnight - now;

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            _sink?.Inner?.CheckRollover(DateTime.Today);
        }
    }
}
