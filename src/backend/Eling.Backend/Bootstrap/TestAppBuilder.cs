using Eling.Core;
using Eling.Core.Scope;
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
        var userScope = UserScope.Resolve(Path.Combine(tempDir, "user_scope"));
        Environment.SetEnvironmentVariable("ELING_AGENT_DATA_DIR", Path.Combine(tempDir, "agent_data"));
        // Isolate the shared data store too. Without this, ElingPaths falls back
        // to the real ~/.local/share/eling and every call mints a new codebase
        // DB file there (the name is a hash of this throwaway temp path), so the
        // user's global store fills with eling-tests-* indexes.
        Environment.SetEnvironmentVariable("ELING_DATA_DIR", Path.Combine(tempDir, "data"));
        var context = new ProjectContext(
            chain,
            userScope,
            Path.Combine(tempDir, ".eling"),
            IsUserHome: false);

        var shared = AppServices.Create(context);
        return Create(shared, context, dashboardPort, shared.LoggerFactory);
    }
}
