using System.Text.Json;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests.Mcp;

public sealed class LogToolsTests : IDisposable
{
    private readonly string _logsDir;

    public LogToolsTests()
    {
        _logsDir = Path.Combine(Path.GetTempPath(), $"eling-logtools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_logsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_logsDir, recursive: true); } catch (Exception ex) { Console.Error.WriteLine($"LogToolsTests: cleanup failed: {ex}"); }
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    [Fact]
    public void ListLogs_EmptyDirectory_ReturnsEmpty()
    {
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance);
        var doc = Parse(tool.ListLogs());
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ListLogs_FiltersByStream()
    {
        File.WriteAllText(Path.Combine(_logsDir, "backend.log"), "hi");
        File.WriteAllText(Path.Combine(_logsDir, "desktop.log"), "hi");
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance);
        var doc = Parse(tool.ListLogs("backend"));
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("backend.log", doc.RootElement.GetProperty("files")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void ReadLog_MissingFile_ReturnsNotFound()
    {
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance);
        var doc = Parse(tool.ReadLog("backend", "2020-01-01"));
        Assert.True(doc.RootElement.TryGetProperty("code", out var code));
        Assert.Equal("not_found", code.GetString());
    }

    [Fact]
    public void ReadLog_TailsAndFilters()
    {
        var content = string.Join("\n", new[]
        {
            "2026-10-23 10:00:00 [INFO] hello",
            "2026-10-23 10:00:01 [ERROR] boom",
            "2026-10-23 10:00:02 [INFO] world",
        });
        File.WriteAllText(Path.Combine(_logsDir, "backend.log"), content);
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance);

        var doc = Parse(tool.ReadLog("backend", null, 10, "ERROR", null));
        Assert.Equal(1, doc.RootElement.GetProperty("returned").GetInt32());
        Assert.Contains("boom", doc.RootElement.GetProperty("lines")[0].GetString());

        var doc2 = Parse(tool.ReadLog("backend", null, 2, null, null));
        Assert.Equal(2, doc2.RootElement.GetProperty("returned").GetInt32());
    }

    [Fact]
    public void ReadLog_JsonlConfig_ReadsJsonlActiveFile()
    {
        File.WriteAllText(Path.Combine(_logsDir, "backend.jsonl"),
            "{\"Timestamp\":\"2026-11-11T09:00:00.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"jsonl-active-marker\"}\n");
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance, () => LogFormat.Jsonl);

        var doc = Parse(tool.ReadLog("backend"));

        Assert.Equal(1, doc.RootElement.GetProperty("returned").GetInt32());
        Assert.Contains("jsonl-active-marker", doc.RootElement.GetProperty("lines")[0].GetString());
    }

    [Fact]
    public void ReadLog_ResponseReportsFormatAndFiles()
    {
        File.WriteAllText(Path.Combine(_logsDir, "backend.jsonl"),
            "{\"Timestamp\":\"2026-11-11T09:00:00.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"jsonl-format-marker\"}\n");
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance, () => LogFormat.Jsonl);

        var doc = Parse(tool.ReadLog("backend"));

        Assert.Equal("jsonl", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal("backend.jsonl", doc.RootElement.GetProperty("files")[0].GetString());
    }

    [Fact]
    public void ReadLog_ExplicitFormat_OverridesConfig()
    {
        File.WriteAllText(Path.Combine(_logsDir, "backend.log"),
            "2026-11-11 09:00:00.000 +07:00 [INF] text-active-marker\n");
        File.WriteAllText(Path.Combine(_logsDir, "backend.jsonl"),
            "{\"Timestamp\":\"2026-11-11T09:00:00.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"jsonl-active-marker\"}\n");
        // Config says Text; the explicit 'jsonl' override selects the live file.
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance, () => LogFormat.Text);

        var doc = Parse(tool.ReadLog("backend", null, 200, null, null, "jsonl"));

        var files = doc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("backend.jsonl", files);
        Assert.DoesNotContain("backend.log", files);
        Assert.Equal("jsonl", doc.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public void ReadLog_ExplicitTextFormat_OverridesJsonlConfig()
    {
        // Config says Jsonl; the explicit 'text' override selects the live .log file.
        File.WriteAllText(Path.Combine(_logsDir, "backend.log"),
            "2026-11-11 09:00:00.000 +07:00 [INF] text-active-marker\n");
        File.WriteAllText(Path.Combine(_logsDir, "backend.jsonl"),
            "{\"Timestamp\":\"2026-11-11T09:00:00.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"jsonl-active-marker\"}\n");
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance, () => LogFormat.Jsonl);

        var doc = Parse(tool.ReadLog("backend", null, 200, null, null, "text"));

        var files = doc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("backend.log", files);
        Assert.DoesNotContain("backend.jsonl", files);
        Assert.Equal("text", doc.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public void ReadLog_InvalidFormat_ReturnsInvalidArgument()
    {
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance);

        var doc = Parse(tool.ReadLog("backend", null, 200, null, null, "yaml"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_argument", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void ReadLog_AutoFormat_EqualsDefault()
    {
        File.WriteAllText(Path.Combine(_logsDir, "backend.log"),
            "2026-11-11 09:00:00.000 +07:00 [INF] auto-format-marker\n");
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance);

        var withAuto = Parse(tool.ReadLog("backend", null, 200, null, null, "auto"));
        var omitted = Parse(tool.ReadLog("backend"));

        Assert.Equal("text", withAuto.RootElement.GetProperty("format").GetString());
        Assert.Equal(omitted.RootElement.GetProperty("format").GetString(), withAuto.RootElement.GetProperty("format").GetString());
        Assert.Equal(omitted.RootElement.GetProperty("returned").GetInt32(), withAuto.RootElement.GetProperty("returned").GetInt32());
        Assert.Contains("auto-format-marker", withAuto.RootElement.GetProperty("lines")[0].GetString());
    }

    [Fact]
    public void ReadLog_NoGetFormat_DefaultsToText()
    {
        File.WriteAllText(Path.Combine(_logsDir, "backend.log"),
            "2026-11-11 09:00:00.000 +07:00 [INF] default-text-marker\n");
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance);

        var doc = Parse(tool.ReadLog("backend"));

        Assert.Equal(1, doc.RootElement.GetProperty("returned").GetInt32());
        Assert.Equal("text", doc.RootElement.GetProperty("format").GetString());
        Assert.Contains("default-text-marker", doc.RootElement.GetProperty("lines")[0].GetString());
    }

    [Fact]
    public void ReadLog_DatedReadSpansBothExtensions_EvenWithExplicitFormat()
    {
        File.WriteAllText(Path.Combine(_logsDir, "backend-2026-11-01.log"),
            "2026-11-01 09:00:00.000 +07:00 [INF] text-half\n");
        File.WriteAllText(Path.Combine(_logsDir, "backend-2026-11-01.jsonl"),
            "{\"Timestamp\":\"2026-11-01T10:00:00.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"jsonl-half\"}\n");
        var tool = new LogTools(_logsDir, NullLogger<LogTools>.Instance);

        var doc = Parse(tool.ReadLog("backend", "2026-11-01", 200, null, null, "text"));

        var lines = doc.RootElement.GetProperty("lines").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains(lines, l => l.Contains("text-half"));
        Assert.Contains(lines, l => l.Contains("jsonl-half"));
        Assert.Equal(new[] { "backend-2026-11-01.log", "backend-2026-11-01.jsonl" },
            doc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToArray());
    }
}
