using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace Eling.Backend.Mcp;

public static class McpLoggingExtensions
{
    private const string DefaultRootPath = ".eling";

    public static IServiceCollection AddElingLogging(this IServiceCollection services, string rootPath = DefaultRootPath)
    {
        // Resolve to absolute path so log directory is deterministic regardless of
        // the host process's current working directory. Without this, a relative
        // rootPath (e.g. ".eling") would be combined with the host CWD and produce
        // log files at unpredictable locations like <user-home>/.eling/logs/.
        var absoluteRootPath = Path.GetFullPath(rootPath);
        var logsDirectory = Path.Combine(absoluteRootPath, "logs");
        Directory.CreateDirectory(logsDirectory);

        var sink = new RollingDailyFileSink(logsDirectory);
        services.AddSingleton(sink);
        services.AddHostedService(sp => new DailyLogRollerService(logsDirectory, sp.GetService<RollingDailyFileSink>()));

        return services.AddSerilog((_, lc) => lc
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.Sink(sink));
    }
}