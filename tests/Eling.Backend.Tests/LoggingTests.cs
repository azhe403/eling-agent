using System.Text;
using Eling.Backend.Mcp;
using Eling.Backend.Dtos;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Eling.Backend.Tests;

public class LoggingTests : IDisposable
{
    private readonly string _tempLogsDir;

    public LoggingTests()
    {
        _tempLogsDir = Path.Combine(Path.GetTempPath(), "eling-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempLogsDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempLogsDir))
            {
                Directory.Delete(_tempLogsDir, true);
            }
        }
        catch { }
    }

    private static LogEvent CreateLogEvent(DateTimeOffset timestamp, string message)
    {
        var parser = new MessageTemplateParser();
        var template = parser.Parse(message);
        return new LogEvent(
            timestamp,
            LogEventLevel.Information,
            null,
            template,
            Enumerable.Empty<LogEventProperty>());
    }

    private static string ReadFileShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Sink_WritesCurrentLogsTo_McpLog()
    {
        using var sink = new RollingDailyFileSink(_tempLogsDir);
        var evt = CreateLogEvent(DateTimeOffset.Now, "Test active log message");

        sink.Emit(evt);

        var activePath = Path.Combine(_tempLogsDir, "mcp.log");
        Assert.True(File.Exists(activePath));
        var content = ReadFileShared(activePath);
        Assert.Contains("Test active log message", content);
    }

    [Fact]
    public void Sink_RendersProcessIdInLogLines()
    {
        using var sink = new RollingDailyFileSink(_tempLogsDir);
        using var logger = new LoggerConfiguration()
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("PID tagged message");

        var activePath = Path.Combine(_tempLogsDir, "mcp.log");
        var content = ReadFileShared(activePath);
        Assert.Contains($"[pid:{Environment.ProcessId}]", content);
        Assert.Contains("PID tagged message", content);
    }

    [Fact]
    public void Rollover_ArchivesPreviousDayToDateNamedFile_AndKeepsMcpLog()
    {
        var day1 = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.FromHours(7));
        var day2 = new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.FromHours(7));

        using var sink = new RollingDailyFileSink(_tempLogsDir);

        // Emit day 1 event
        sink.Emit(CreateLogEvent(day1, "Message on Day 1"));

        var activePath = Path.Combine(_tempLogsDir, "mcp.log");
        Assert.True(File.Exists(activePath));
        Assert.Contains("Message on Day 1", ReadFileShared(activePath));

        // Emit day 2 event (triggers rollover)
        sink.Emit(CreateLogEvent(day2, "Message on Day 2"));

        var archiveDay1 = Path.Combine(_tempLogsDir, "mcp-2026-08-13.log");
        Assert.True(File.Exists(archiveDay1), "mcp-2026-08-13.log should exist after rollover");

        var archiveContent = ReadFileShared(archiveDay1);
        Assert.Contains("Message on Day 1", archiveContent);

        var activeContent = ReadFileShared(activePath);
        Assert.Contains("Message on Day 2", activeContent);
        Assert.DoesNotContain("Message on Day 1", activeContent);
    }

    [Fact]
    public void Startup_SeparatesExistingMixedLogsIntoArchiveAndActive()
    {
        var activePath = Path.Combine(_tempLogsDir, "mcp.log");
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var existingContent = new StringBuilder()
            .AppendLine($"{yesterday} 22:48:27.628 +07:00 [INF] [Test] Yesterday log line 1")
            .AppendLine($"{yesterday} 22:49:00.000 +07:00 [INF] [Test] Yesterday log line 2")
            .AppendLine($"{today} 06:23:44.145 +07:00 [INF] [Test] Today log line 1")
            .ToString();

        File.WriteAllText(activePath, existingContent, Encoding.UTF8);

        // Creating the sink on 2026-08-14 will trigger startup rollover
        using var sink = new RollingDailyFileSink(_tempLogsDir);

        var archivePath = Path.Combine(_tempLogsDir, $"mcp-{yesterday}.log");
        Assert.True(File.Exists(archivePath));

        var archiveLines = ReadFileShared(archivePath);
        Assert.Contains("Yesterday log line 1", archiveLines);
        Assert.Contains("Yesterday log line 2", archiveLines);
        Assert.DoesNotContain("Today log line 1", archiveLines);

        var activeLines = ReadFileShared(activePath);
        Assert.Contains("Today log line 1", activeLines);
        Assert.DoesNotContain("Yesterday log line", activeLines);
    }

    [Fact]
    public void Prune_DeletesArchivedLogsOlderThanRetainedDays()
    {
        var oldDate = DateTime.Today.AddDays(-10).ToString("yyyy-MM-dd");
        var recentDate = DateTime.Today.AddDays(-3).ToString("yyyy-MM-dd");

        var oldFile = Path.Combine(_tempLogsDir, $"mcp-{oldDate}.log");
        var recentFile = Path.Combine(_tempLogsDir, $"mcp-{recentDate}.log");

        File.WriteAllText(oldFile, "old log");
        File.WriteAllText(recentFile, "recent log");

        using var sink = new RollingDailyFileSink(_tempLogsDir, retainedDays: 7);

        Assert.False(File.Exists(oldFile), "Old log file beyond 7 days should be pruned");
        Assert.True(File.Exists(recentFile), "Recent log file within 7 days should be kept");
    }
}
