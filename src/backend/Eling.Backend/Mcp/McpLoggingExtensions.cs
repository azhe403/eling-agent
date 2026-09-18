using Eling.Core;
using Eling.Core.Logging;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

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

        var sink = ElingLoggingConfig.CreateSink(logsDirectory, "backend.log", "backend");
        services.AddSingleton(sink);
        services.AddHostedService(sp => new DailyLogRollerService(logsDirectory, sp.GetService<RollingDailyFileSink>()));

        return services.AddSerilog((_, lc) => lc
            .ConfigureElingDefaults(sink, projectId: projectId)
            .WriteTo.Console(
                outputTemplate: ElingLoggingConfig.DefaultOutputTemplate,
                standardErrorFromLevel: LogEventLevel.Verbose));
    }
}
