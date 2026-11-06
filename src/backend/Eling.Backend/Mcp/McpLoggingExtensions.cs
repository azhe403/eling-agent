using Eling.Backend.Logging;
using Eling.Core;
using Eling.Core.Logging;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Eling.Backend.Mcp;

public static class McpLoggingExtensions
{
    public static IServiceCollection AddElingLogging(
        this IServiceCollection services,
        string? rootPath = null,
        string? projectId = null)
    {
        var logsDirectory = !string.IsNullOrWhiteSpace(rootPath)
            ? (Path.GetFileName(rootPath).Equals("logs", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(rootPath)
                : Path.Combine(Path.GetFullPath(rootPath), "logs"))
            : CentralLogDirectory.Resolve();

        Directory.CreateDirectory(logsDirectory);

        // Text is only the starting point; a stored preference is applied by the
        // switcher (the dashboard host does that on startup and on every PUT).
        var initialSink = ElingLoggingConfig.CreateSink(logsDirectory, "backend.log", "backend", 7, LogFormat.Text);
        var forwarder = new LogFormatForwardingSink(initialSink);

        // The forwarder is what Serilog gets: it survives a format switch, so
        // resolving the logger once is enough.
        services.AddSingleton(forwarder);
        services.AddSingleton(sp => new LogFormatSwitcher(
            forwarder,
            logsDirectory,
            projectId,
            Environment.ProcessId,
            sp.GetRequiredService<ILogger<LogFormatSwitcher>>()));
        services.AddHostedService(sp => new DailyLogRollerService(logsDirectory, sp.GetService<LogFormatForwardingSink>()));

        return services.AddSerilog((_, lc) => LogFormatSwitcher.ConfigureHostLogging(lc, forwarder, projectId, Environment.ProcessId));
    }
}
