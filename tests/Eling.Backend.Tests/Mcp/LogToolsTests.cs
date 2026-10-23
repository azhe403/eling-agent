using System.Text.Json;
using Eling.Backend.Mcp.Tools;
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
        try { Directory.Delete(_logsDir, recursive: true); } catch { }
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
}
