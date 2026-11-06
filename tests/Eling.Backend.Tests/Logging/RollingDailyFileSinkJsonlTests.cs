using System.Text;
using Eling.Core;
using Eling.Core.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;
using Xunit;

namespace Eling.Backend.Tests.Logging;

public sealed class RollingDailyFileSinkJsonlTests : IDisposable
{
    private readonly string _tempLogsDir;

    public RollingDailyFileSinkJsonlTests()
    {
        _tempLogsDir = Path.Combine(Path.GetTempPath(), "eling-jsonl-test-" + Guid.NewGuid().ToString("N"));
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

    private static string ReadFileShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void JsonlSink_ArchivesToJsonl_WithJsonlDateRegex()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var yesterdayKey = yesterday.ToString("yyyy-MM-dd");

        // Seed an active backend.jsonl that holds a prior-day JSONL line plus today's.
        // JsonFormatter (Serilog 4.4 core) emits "Timestamp"/"Level"/"MessageTemplate"
        // (NOT the @t/@lt OTel-compact names), so the seed uses real field names.
        var activePath = Path.Combine(_tempLogsDir, "backend.jsonl");
        File.WriteAllText(activePath,
            $"{{\"Timestamp\":\"{yesterdayKey}T10:00:00.0000000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"Yesterday jsonl line\"}}\n" +
            $"{{\"Timestamp\":\"{DateTime.Today:yyyy-MM-dd}T10:00:00.0000000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"Today jsonl line\"}}\n",
            Encoding.UTF8);

        using var sink = ElingLoggingConfig.CreateSink(
            _tempLogsDir, "backend.jsonl", "backend", 7, LogFormat.Jsonl);

        // Trigger startup rollover already ran in ctor; assert the archive exists under .jsonl.
        var archivePath = Path.Combine(_tempLogsDir, $"backend-{yesterdayKey}.jsonl");
        Assert.True(File.Exists(archivePath), "JSONL archive should be created with .jsonl extension");
        Assert.Contains("Yesterday jsonl line", ReadFileShared(archivePath));
        Assert.DoesNotContain("Today jsonl line", ReadFileShared(archivePath));

        var activeContent = ReadFileShared(activePath);
        Assert.Contains("Today jsonl line", activeContent);
        Assert.DoesNotContain("Yesterday jsonl line", activeContent);
    }

    [Fact]
    public void JsonlSink_RolloverMovesYesterdayLineToArchive()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var yesterdayKey = yesterday.Date.ToString("yyyy-MM-dd");

        // Seed the active file with one JSONL line carrying yesterday's Timestamp date.
        var activePath = Path.Combine(_tempLogsDir, "backend.jsonl");
        File.WriteAllText(activePath,
            $"{{\"Timestamp\":\"{yesterdayKey}T10:00:00.0000000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"Yesterday jsonl line\"}}\n",
            Encoding.UTF8);

        using var sink = ElingLoggingConfig.CreateSink(
            _tempLogsDir, "backend.jsonl", "backend", 7, LogFormat.Jsonl);
        // Ctor runs startup rollover: groups yesterday's line into backend-{yesterday}.jsonl.

        var archivePath = Path.Combine(_tempLogsDir, $"backend-{yesterdayKey}.jsonl");
        Assert.True(File.Exists(archivePath), "Rolled-over JSONL event should be archived with .jsonl extension");
        Assert.Contains("Yesterday jsonl line", ReadFileShared(archivePath));
        Assert.DoesNotContain("Yesterday jsonl line", ReadFileShared(activePath));
    }

    [Fact]
    public void PruneOldLogs_RemovesBothExtensions()
    {
        var oldDate = DateTime.Today.AddDays(-10).ToString("yyyy-MM-dd");
        var oldLog = Path.Combine(_tempLogsDir, $"backend-{oldDate}.log");
        var oldJsonl = Path.Combine(_tempLogsDir, $"backend-{oldDate}.jsonl");

        File.WriteAllText(oldLog, "old log", Encoding.UTF8);
        File.WriteAllText(oldJsonl, "old jsonl", Encoding.UTF8);

        using var sink = ElingLoggingConfig.CreateSink(
            _tempLogsDir, "backend.jsonl", "backend", 7, LogFormat.Jsonl);

        Assert.False(File.Exists(oldLog), "Old .log archive should be pruned");
        Assert.False(File.Exists(oldJsonl), "Old .jsonl archive should be pruned");
    }

    [Fact]
    public void RealJsonFormatterOutput_RolloverPicksUpTimestampField()
    {
        // Prove the sink works against real JsonFormatter output, not just
        // hand-seeded lines: emit a yesterday-dated event through the actual
        // formatter, trigger rollover, assert the archive captured it.
        var yesterday = DateTime.Today.AddDays(-1);
        var yesterdayKey = yesterday.Date.ToString("yyyy-MM-dd");

        using var sink = ElingLoggingConfig.CreateSink(
            _tempLogsDir, "backend.jsonl", "backend", 7, LogFormat.Jsonl);

        var parser = new Serilog.Parsing.MessageTemplateParser();
        // Build a DateTimeOffset stamped yesterday (local) so the emitted
        // JSONL line's "Timestamp" field carries yesterday's date.
        var yesterdayDto = new DateTimeOffset(yesterday.Date.AddHours(10), TimeSpan.FromHours(7));
        var yesterdayEvt = new LogEvent(
            yesterdayDto,
            LogEventLevel.Information,
            null,
            parser.Parse("Real formatter yesterday line {X}"),
            new[] { new LogEventProperty("X", new ScalarValue(1)) });

        sink.Emit(yesterdayEvt);

        // Force rollover to today's date to move the yesterday-dated line to archive.
        sink.CheckRollover(DateTime.Today);

        var archivePath = Path.Combine(_tempLogsDir, $"backend-{yesterdayKey}.jsonl");
        var activePath = Path.Combine(_tempLogsDir, "backend.jsonl");
        Assert.True(File.Exists(archivePath), "Yesterday line should be archived after real-emit rollover");
        Assert.Contains("Real formatter yesterday line", ReadFileShared(archivePath));
        Assert.DoesNotContain("Real formatter yesterday line", ReadFileShared(activePath));
    }

    [Fact]
    public void TextSink_KeepsExistingBehaviour()
    {
        using var sink = ElingLoggingConfig.CreateSink(
            _tempLogsDir, "backend.log", "backend", 7, LogFormat.Text);

        var parser = new Serilog.Parsing.MessageTemplateParser();
        var evt = new LogEvent(
            DateTimeOffset.Now,
            LogEventLevel.Information,
            null,
            parser.Parse("Text sink regression message"),
            System.Linq.Enumerable.Empty<LogEventProperty>());

        sink.Emit(evt);

        var activePath = Path.Combine(_tempLogsDir, "backend.log");
        Assert.True(File.Exists(activePath));
        var content = ReadFileShared(activePath);
        Assert.Contains("Text sink regression message", content);
        Assert.Contains("[INF]", content); // text template keeps level tokens
    }
}
