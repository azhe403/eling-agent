using Eling.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

public sealed class RuntimeRegistrationService : IHostedService
{
    private readonly RuntimeRegistry _registry;
    private readonly ProjectContext _context;
    private readonly ILogger<RuntimeRegistrationService> _logger;

    public RuntimeRegistrationService(
        RuntimeRegistry registry,
        ProjectContext context,
        ILogger<RuntimeRegistrationService> logger)
    {
        _registry = registry;
        _context = context;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var registration = RuntimeSelfRegistration.Build(_context);
        _registry.Register(registration);
        _logger.LogInformation("Self-registered runtime for process {Pid}", Environment.ProcessId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _registry.Unregister(Environment.ProcessId);
            _logger.LogInformation("Unregistered runtime for process {Pid}", Environment.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister runtime for process {Pid}", Environment.ProcessId);
        }

        return Task.CompletedTask;
    }
}
