using Serilog;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Eling.Core.Logging;

public static class ElingLoggingConfig
{
    public const string DefaultOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [pid:{ProcessId}] [project:{ProjectId}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}";

    public static MessageTemplateTextFormatter CreateDefaultFormatter(string? outputTemplate = null) =>
        new(outputTemplate ?? DefaultOutputTemplate);

    public static RollingDailyFileSink CreateSink(
        string logsDirectory,
        string activeFileName = "eling.log",
        string archivePrefix = "eling",
        int retainedDays = 7,
        string? outputTemplate = null)
    {
        var formatter = CreateDefaultFormatter(outputTemplate);
        return new RollingDailyFileSink(
            logsDirectory,
            activeFileName,
            archivePrefix,
            retainedDays,
            formatter);
    }

    public static LoggerConfiguration ConfigureElingDefaults(
        this LoggerConfiguration configuration,
        RollingDailyFileSink sink,
        string? projectId = null,
        int? processId = null)
    {
        var effectiveProjectId = string.IsNullOrWhiteSpace(projectId) ? "unknown" : projectId;
        var effectiveProcessId = processId ?? Environment.ProcessId;

        return configuration
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ProcessId", effectiveProcessId)
            .Enrich.WithProperty("ProjectId", effectiveProjectId)
            .Enrich.WithProperty("CorrelationId", "-")
            .WriteTo.Sink(sink);
    }
}
