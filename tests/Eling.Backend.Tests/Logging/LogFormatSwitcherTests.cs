using System.Text;
using Eling.Backend.Logging;
using Eling.Core.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Eling.Backend.Tests.Logging;

/// <summary>
/// The format switch is the one place a running host changes where its log
/// lines land, so these tests pin the file-level outcome of each transition,
/// not just the swap call: what got archived, what the new active file holds,
/// and whether the static <c>Serilog.Log</c> logger followed.
/// </summary>
/// <remarks>
/// Sharing <see cref="ElingDataDirCollection"/> is not about the env var here:
/// these tests reassign the process-global <c>Serilog.Log.Logger</c>, and the
/// other host-level logging test builds a real host whose output would be
/// redirected mid-run if the two ran in parallel.
/// </remarks>
[Collection(ElingDataDirCollection.Name)]
public sealed class LogFormatSwitcherTests : IDisposable
{
    private const string ArchivePrefix = "backend";

    private readonly string _logsDirectory;
    private readonly LogFormatForwardingSink _forwarder;
    private readonly LogFormatSwitcher _switcher;
    private readonly Serilog.ILogger _originalStaticLogger;

    public LogFormatSwitcherTests()
    {
        _logsDirectory = Path.Combine(Path.GetTempPath(), "eling-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logsDirectory);

        var initialSink = ElingLoggingConfig.CreateSink(_logsDirectory, "backend.log", ArchivePrefix, 7, LogFormat.Text);
        _forwarder = new LogFormatForwardingSink(initialSink);
        _switcher = new LogFormatSwitcher(
            _forwarder,
            _logsDirectory,
            projectId: "test-project",
            processId: Environment.ProcessId,
            NullLogger<LogFormatSwitcher>.Instance);

        _originalStaticLogger = Serilog.Log.Logger;
    }

    public void Dispose()
    {
        // Restore before closing so no other test logs into the temp directory.
        Serilog.Log.Logger = _originalStaticLogger;
        _switcher.Dispose();
        _forwarder.Dispose();

        try
        {
            if (Directory.Exists(_logsDirectory))
            {
                Directory.Delete(_logsDirectory, true);
            }
        }
        catch { }
    }

    private string Today => DateTime.Today.ToString("yyyy-MM-dd");

    private string ActivePath(LogFormat format) =>
        Path.Combine(_logsDirectory, format == LogFormat.Jsonl ? "backend.jsonl" : "backend.log");

    private string ArchivePath(LogFormat format) =>
        Path.Combine(_logsDirectory, $"{ArchivePrefix}-{Today}{(format == LogFormat.Jsonl ? ".jsonl" : ".log")}");

    private static LogEvent Probe(string marker)
    {
        var parser = new MessageTemplateParser();
        return new LogEvent(
            DateTimeOffset.Now,
            LogEventLevel.Information,
            null,
            parser.Parse(marker),
            Enumerable.Empty<LogEventProperty>());
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Apply_WithSameFormat_IsNoOp()
    {
        _forwarder.Emit(Probe("before-noop"));
        var activeBefore = _forwarder.Inner;

        _switcher.Apply(LogFormat.Text);

        Assert.Same(activeBefore, _forwarder.Inner);
        Assert.Equal(LogFormat.Text, _forwarder.Inner!.Format);
        Assert.False(File.Exists(ArchivePath(LogFormat.Text)));
        Assert.Contains("before-noop", ReadShared(ActivePath(LogFormat.Text)));
    }

    [Fact]
    public void Apply_Jsonl_ArchivesTextActiveFile_AndWritesJsonl()
    {
        _forwarder.Emit(Probe("text-era-line"));

        _switcher.Apply(LogFormat.Jsonl);

        Assert.Equal(LogFormat.Jsonl, _forwarder.Inner!.Format);
        Assert.Contains("text-era-line", ReadShared(ArchivePath(LogFormat.Text)));
        Assert.False(File.Exists(ActivePath(LogFormat.Text)));

        _forwarder.Emit(Probe("jsonl-era-line"));

        Assert.Contains("jsonl-era-line", ReadShared(ActivePath(LogFormat.Jsonl)));
        Assert.DoesNotContain("jsonl-era-line", ReadShared(ArchivePath(LogFormat.Text)));
    }

    [Fact]
    public void Apply_TextAfterJsonl_ArchivesJsonlActiveFile()
    {
        _switcher.Apply(LogFormat.Jsonl);
        _forwarder.Emit(Probe("jsonl-era-line"));

        _switcher.Apply(LogFormat.Text);

        Assert.Equal(LogFormat.Text, _forwarder.Inner!.Format);
        Assert.Contains("jsonl-era-line", ReadShared(ArchivePath(LogFormat.Jsonl)));
        Assert.False(File.Exists(ActivePath(LogFormat.Jsonl)));

        _forwarder.Emit(Probe("text-again-line"));

        Assert.Contains("text-again-line", ReadShared(ActivePath(LogFormat.Text)));
        // The line archived on the way out must not be echoed into the new file.
        Assert.DoesNotContain("jsonl-era-line", ReadShared(ActivePath(LogFormat.Text)));
    }

    [Fact]
    public void Apply_SwapsStaticSerilogLogger_SoStaticWritesLandInNewFile()
    {
        var loggerBefore = Serilog.Log.Logger;
        _switcher.Apply(LogFormat.Jsonl);

        Assert.NotSame(loggerBefore, Serilog.Log.Logger);

        Serilog.Log.Information("static-marker");

        Assert.Contains("static-marker", ReadShared(ActivePath(LogFormat.Jsonl)));
    }

    [Fact]
    public void Apply_SecondSwitch_DisposesLoggerItInstalled()
    {
        _switcher.Apply(LogFormat.Jsonl);
        var firstInstalled = Serilog.Log.Logger;

        _switcher.Apply(LogFormat.Text);

        // The rebuilt logger keeps a disposed marker: writes after Dispose are dropped.
        firstInstalled.Information("stale-marker");

        Assert.DoesNotContain("stale-marker", ReadShared(ArchivePath(LogFormat.Jsonl)));
        Assert.DoesNotContain("stale-marker", ReadShared(ActivePath(LogFormat.Text)));
    }
}
