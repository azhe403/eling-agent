using Eling.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

public sealed class HttpLoopService : BackgroundService
{
    private readonly AppServices _shared;
    private readonly ProjectContext _context;
    private readonly HttpLoopOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public HttpLoopService(
        AppServices shared,
        ProjectContext context,
        HttpLoopOptions options,
        ILoggerFactory loggerFactory)
    {
        _shared = shared;
        _context = context;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var isDevMode = await DevModeDetector.IsDevModeAsync(stoppingToken);
        await HttpLoop.RunAsync(
            _shared,
            _context,
            _options.DashboardPort,
            isDevMode,
            _loggerFactory,
            stoppingToken);
    }
}
