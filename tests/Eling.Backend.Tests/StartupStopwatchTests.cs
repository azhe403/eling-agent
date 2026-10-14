using System.Text.RegularExpressions;
using Eling.Backend.Bootstrap;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// One deterministic flow, because <see cref="StartupStopwatch"/> keeps
/// process-lifetime state: the first <see cref="StartupStopwatch.AttachLogger"/>
/// wins and only the first <see cref="StartupStopwatch.MarkMcpReady"/> emits a
/// summary. Splitting those across separate facts would make the outcome depend
/// on xUnit's execution order.
/// </summary>
public class StartupStopwatchTests
{
    private class TestLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public void Startup_Sequence_Buffers_Marks_Then_Flushes_And_Summarizes_Once()
    {
        // A mark recorded before any logger is attached is buffered.
        StartupStopwatch.Mark("test-buffered");

        var logger = new TestLogger();
        StartupStopwatch.AttachLogger(logger);

        // The buffered mark is flushed on attach.
        Assert.Contains(logger.Logs, l =>
            l.Level == LogLevel.Information
            && l.Message.Contains("StartupTiming: phase=test-buffered")
            && l.Message.Contains("elapsedMs="));

        // Attaching again is a no-op: the first logger owns the process lifetime.
        var second = new TestLogger();
        StartupStopwatch.AttachLogger(second);
        Assert.DoesNotContain(second.Logs, l => l.Message.Contains("StartupTiming"));

        // Marks after attach are logged immediately, with non-decreasing elapsed.
        StartupStopwatch.Mark("test-live");

        var elapsed = logger.Logs
            .Where(l => l.Message.Contains("StartupTiming: phase=test-"))
            .Select(l => long.Parse(Regex.Match(l.Message, @"elapsedMs=(\d+)").Groups[1].Value))
            .ToList();

        Assert.Equal(2, elapsed.Count);
        Assert.True(elapsed[0] <= elapsed[1], $"expected non-decreasing elapsed, got {string.Join(", ", elapsed)}");

        // The MCP-ready mark logs exactly one summary with the phase breakdown.
        StartupStopwatch.MarkMcpReady();

        var summaries = logger.Logs
            .Where(l => l.Message.Contains("StartupTiming: SUMMARY mcp-ready"))
            .ToList();

        Assert.Single(summaries);
        Assert.Contains("totalMs=", summaries[0].Message, StringComparison.Ordinal);
        Assert.Contains("mcp-catalog-done", summaries[0].Message, StringComparison.Ordinal);
        Assert.Contains("->", summaries[0].Message, StringComparison.Ordinal);

        // Repeated MCP-ready marks never emit a second summary.
        StartupStopwatch.MarkMcpReady();

        Assert.Single(logger.Logs
            .Where(l => l.Message.Contains("StartupTiming: SUMMARY mcp-ready"))
            .ToList());
    }
}
