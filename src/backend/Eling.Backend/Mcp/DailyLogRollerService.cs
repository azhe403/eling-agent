using Microsoft.Extensions.Hosting;

namespace Eling.Backend.Mcp;

public sealed class DailyLogRollerService : BackgroundService
{
    private readonly string _logsDirectory;
    private readonly RollingDailyFileSink? _sink;

    public DailyLogRollerService(string logsDirectory, RollingDailyFileSink? sink = null)
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

            _sink?.CheckRollover(DateTime.Today);
        }
    }
}