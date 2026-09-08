using Eling.Core;
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

        var sink = new RollingDailyFileSink(logsDirectory);
        services.AddSingleton(sink);
        services.AddHostedService(sp => new DailyLogRollerService(logsDirectory, sp.GetService<RollingDailyFileSink>()));

        var effectiveProjectId = projectId ?? "unknown";

        return services.AddSerilog((_, lc) => lc
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .Enrich.WithProperty("ProjectId", effectiveProjectId)
            .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.Sink(sink));
    }
}
