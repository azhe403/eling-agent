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
    /// <param name="rootPath">Optional override for the logs directory root.</param>
    /// <param name="projectId">Identifier stamped on every log line.</param>
    /// <param name="initialFormat">
    /// The sink's starting format. <see cref="LogFormat.Text"/> keeps the
    /// historical behaviour for callers that do not care (e.g. the dashboard
    /// host before it reads the stored preference); hosts that honour the
    /// user's saved setting pass it through so the first sink matches it.
    /// </param>
    public static IServiceCollection AddElingLogging(
        this IServiceCollection services,
        string? rootPath = null,
        string? projectId = null,
        LogFormat initialFormat = LogFormat.Text)
    {
        var logsDirectory = !string.IsNullOrWhiteSpace(rootPath)
            ? (Path.GetFileName(rootPath).Equals("logs", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(rootPath)
                : Path.Combine(Path.GetFullPath(rootPath), "logs"))
            : CentralLogDirectory.Resolve();

        Directory.CreateDirectory(logsDirectory);

        // The initial sink matches the stored preference; a later switch
        // (the dashboard host does that on startup and on every PUT) is
        // applied by the switcher, not here.
        var initialSink = ElingLoggingConfig.CreateSink(
            logsDirectory,
            initialFormat == LogFormat.Jsonl ? "backend.jsonl" : "backend.log",
            "backend",
            7,
            initialFormat);
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
