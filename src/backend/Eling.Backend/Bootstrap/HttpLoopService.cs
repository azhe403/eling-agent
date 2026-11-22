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
    private readonly ILogger _logger;

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
        _logger = loggerFactory.CreateLogger<HttpLoopService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HttpLoopService starting");
        var isDevMode = DevModeDetector.IsDevMode();
        _logger.LogInformation("HttpLoopService isDevMode={IsDevMode}", isDevMode);
        try
        {
            await HttpLoop.RunAsync(
                _shared,
                _context,
                _options.DashboardPort,
                isDevMode,
                _loggerFactory,
                stoppingToken);
            _logger.LogInformation("HttpLoopService completed normally");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HttpLoopService failed");
            throw;
        }
    }
}
