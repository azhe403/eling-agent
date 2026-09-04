using Eling.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Registers this backend instance as a runtime in the shared
/// <see cref="RuntimeRegistry"/> at startup and unregisters on shutdown.
/// The "UserScope" sentinel is used when the backend runs at user home
/// without an active project repository, so the UI can distinguish
/// global-only sessions from real project sessions.
/// </summary>
public static class RuntimeSelfRegistration
{
    public static RuntimeRegistration Build(ProjectContext context) => new()
    {
        ProcessId = Environment.ProcessId,
        ProjectRoot = context.IsUserHome ? "UserScope" : context.ProjectScope.Root,
        DataDirectory = context.EffectiveDataDir,
        StartTime = ProcessStartTime.Get(),
        McpEnabled = true,
        McpTransport = "stdio"
    };

    public static void Wire(IHost host, RuntimeRegistration registration)
    {
        host.Services.GetRequiredService<RuntimeRegistry>().Register(registration);
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
        {
            try
            {
                host.Services.GetRequiredService<RuntimeRegistry>().Unregister(Environment.ProcessId);
            }
            catch
            {
                // Best effort; peer sweep will clean up.
            }
        });
    }
}
