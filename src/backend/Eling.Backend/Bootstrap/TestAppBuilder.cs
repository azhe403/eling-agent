using Eling.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

public static class TestAppBuilder
{
    public static WebApplication Create(
        AppServices shared,
        ProjectContext context,
        int dashboardPort,
        ILoggerFactory loggerFactory
    )
        => HttpLoop.BuildWebApplication(shared, context, dashboardPort, false, loggerFactory);

    public static WebApplication CreateSelfContained(
        int dashboardPort = 0
    )
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eling-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, ".eling"));

        var chain = ScopeChain.Discover(tempDir);
        var userScope = UserScope.Resolve(Environment.GetEnvironmentVariable("ELING_USER_SCOPE"));
        var context = new ProjectContext(
            chain,
            userScope,
            Path.Combine(tempDir, ".eling"),
            IsUserHome: false);

        var shared = AppServices.Create(context);
        return Create(shared, context, dashboardPort, shared.LoggerFactory);
    }
}
