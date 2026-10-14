using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Attaches the DI logger to <see cref="StartupStopwatch"/> — flushing every
/// mark buffered since process entry — and records the post-hosted-services
/// startup phases.
/// </summary>
/// <remarks>
/// Registered last in <c>McpHostBuilder</c> so its <see cref="StartAsync"/> runs
/// after the other hosted services' StartAsync. That ordering is what the
/// mark below measures: every hosted service has returned from StartAsync. The
/// dashboard port is NOT necessarily bound yet — <c>HttpLoopService</c> is a
/// BackgroundService that binds inside ExecuteAsync, after StartAsync returns —
/// so do not read this mark as "port ready". The MCP-ready mark comes from the
/// tools/list filter, which fires on the client's first catalog request after
/// the initialize handshake.
/// </remarks>
public sealed class StartupTimingHostedService : IHostedService
{
    private readonly ILogger<StartupTimingHostedService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public StartupTimingHostedService(
        ILogger<StartupTimingHostedService> logger,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartupStopwatch.AttachLogger(_logger);
        StartupStopwatch.Mark("hosted-services-started");

        // ApplicationStarted is a CancellationToken (cancelled once every
        // IHostedService has started); register the mark against it rather
        // than subscribing to an event.
        _lifetime.ApplicationStarted.Register(() => StartupStopwatch.Mark("app-started"));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
