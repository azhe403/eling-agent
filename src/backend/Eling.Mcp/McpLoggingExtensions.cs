using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace Eling.Mcp;

public static class McpLoggingExtensions
{
    public static IServiceCollection AddElingLogging(this IServiceCollection services, string logsDirectory = ".eling/logs")
    {
        Directory.CreateDirectory(logsDirectory);
        var logPath = Path.Combine(logsDirectory, "mcp.log");

        // Archive previous day's log if mcp.log already exists from a prior day
        if (File.Exists(logPath))
        {
            var lastWrite = File.GetLastWriteTime(logPath).Date;
            if (lastWrite < DateTime.Today)
            {
                var archivePath = Path.Combine(logsDirectory, $"mcp-{lastWrite:yyyy-MM-dd}.log");
                if (!File.Exists(archivePath))
                {
                    File.Move(logPath, archivePath);
                }
            }
        }

        // Retain up to 7 days of archived logs
        foreach (var oldLog in Directory.GetFiles(logsDirectory, "mcp-*.log"))
        {
            if (File.GetLastWriteTime(oldLog) < DateTime.Now.AddDays(-7))
            {
                try { File.Delete(oldLog); } catch { /* best-effort cleanup */ }
            }
        }

        return services.AddSerilog((_, lc) => lc
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.File(
                path: logPath,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"));
    }
}
