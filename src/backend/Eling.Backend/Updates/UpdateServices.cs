using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Updates;

/// <summary>
/// The single definition of the update-check graph, so the MCP host and the
/// dashboard owner cannot drift apart on timeout or lifetime. Neither host is
/// responsible for the periodic pump: that is added by the production owner
/// only, which is what keeps MCP-only peers off the network.
/// </summary>
internal static class UpdateServices
{
    internal static void Add(IServiceCollection services, UserScope userScope)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(userScope);

        services.TryAddSingleton(sp => new FileUpdateCache(
            userScope,
            sp.GetService<ILoggerFactory>()?.CreateLogger<FileUpdateCache>()
                ?? NullLogger<FileUpdateCache>.Instance));

        services.AddHttpClient<GitHubReleaseClient>(client =>
        {
            client.Timeout = UpdateChecker.HttpTimeout;
        });

        services.TryAddSingleton<IUpdateChecker>(sp => new UpdateChecker(
            sp.GetRequiredService<GitHubReleaseClient>(),
            sp.GetRequiredService<FileUpdateCache>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<UpdateChecker>()
                ?? NullLogger<UpdateChecker>.Instance,
            UpdateChecker.ResolveCurrentVersion()));
    }
}
