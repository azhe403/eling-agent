using System.Text;
using Eling.Core.Logging;
using Xunit;

namespace Eling.Core.Tests;

/// <summary>
/// The dashboard and the MCP log tools read whichever format the user last
/// saved, so the shared reader has to understand both extensions and both
/// level spellings. The JSONL lines below use the fields Serilog core's
/// JsonFormatter actually emits (verified on 4.4.0): plain "Timestamp" and
/// "Level" with the full level name, not the OTel-compact "@t"/"@lt" form.
/// </summary>
public sealed class LogReaderJsonlTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "eling-logreader-jsonl-" + Guid.NewGuid().ToString("N"));

    public LogReaderJsonlTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void ListFiles_ReturnsBothExtensions()
    {
        File.WriteAllText(Path.Combine(_dir, "backend.log"), "x", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_dir, "backend.jsonl"), "x", Encoding.UTF8);

        var files = LogReader.ListFiles(_dir, "backend");

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Name == "backend.log");
        Assert.Contains(files, f => f.Name == "backend.jsonl");
    }

    [Fact]
    public void ListFiles_ArchivesOfBothExtensionsAreListed()
    {
        File.WriteAllText(Path.Combine(_dir, "backend-2026-11-04.log"), "x", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_dir, "backend-2026-11-05.jsonl"), "x", Encoding.UTF8);

        var files = LogReader.ListFiles(_dir, "backend");

        Assert.Equal(2, files.Count);
    }

    [Theory]
    [InlineData(LogFormat.Text, "backend.log")]
    [InlineData(LogFormat.Jsonl, "backend.jsonl")]
    public void ResolvePath_PicksExtensionByFormat(LogFormat format, string expected)
    {
        Assert.Equal(Path.Combine(_dir, expected), LogReader.ResolvePath(_dir, "backend", null, format));
    }

    [Theory]
    [InlineData(LogFormat.Text, "backend-2026-11-05.log")]
    [InlineData(LogFormat.Jsonl, "backend-2026-11-05.jsonl")]
    public void ResolvePath_DatedArchive_PicksExtensionByFormat(LogFormat format, string expected)
    {
        Assert.Equal(Path.Combine(_dir, expected), LogReader.ResolvePath(_dir, "backend", "2026-11-05", format));
    }

    [Fact]
    public void ResolvePath_WithoutFormat_StaysText()
    {
        Assert.Equal(Path.Combine(_dir, "backend.log"), LogReader.ResolvePath(_dir, "backend", null));
    }

    [Theory]
    [InlineData("{\"Timestamp\":\"2026-11-05T00:00:00+07:00\",\"Level\":\"Error\",\"MessageTemplate\":\"x\"}", "ERROR")]
    [InlineData("{\"Timestamp\":\"2026-11-05T00:00:00+07:00\",\"Level\":\"Fatal\",\"MessageTemplate\":\"x\"}", "FATAL")]
    [InlineData("{\"Timestamp\":\"2026-11-05T00:00:00+07:00\",\"Level\":\"Warning\",\"MessageTemplate\":\"x\"}", "WARN")]
    [InlineData("{\"Timestamp\":\"2026-11-05T00:00:00+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"x\"}", "INFO")]
    [InlineData("{\"Timestamp\":\"2026-11-05T00:00:00+07:00\",\"Level\":\"Debug\",\"MessageTemplate\":\"x\"}", "DEBUG")]
    [InlineData("{\"Timestamp\":\"2026-11-05T00:00:00+07:00\",\"Level\":\"Verbose\",\"MessageTemplate\":\"x\"}", "VERBOSE")]
    public void Matches_RecognizesJsonlLevelTokens(string line, string level)
    {
        Assert.True(LogReader.Matches(line, level, contains: null));
    }

    [Fact]
    public void Matches_JsonlLine_DoesNotMatchTheWrongLevel()
    {
        const string errorLine =
            "{\"Timestamp\":\"2026-11-05T00:00:00+07:00\",\"Level\":\"Error\",\"MessageTemplate\":\"x\"}";

        Assert.False(LogReader.Matches(errorLine, "WARN", contains: null));
        Assert.False(LogReader.Matches(errorLine, "INFO", contains: null));
    }

    [Fact]
    public void Matches_StillRecognizesTextTokens()
    {
        const string textLine =
            "2026-11-05 10:00:00.000 +07:00 [ERR] [pid:1] [project:p] [env:Dev] [-] boom";

        Assert.True(LogReader.Matches(textLine, "ERROR", contains: null));
        Assert.False(LogReader.Matches(textLine, "INFO", contains: null));
    }

    [Fact]
    public void Read_JsonlFormat_ReadsTheJsonlFile()
    {
        File.WriteAllText(
            Path.Combine(_dir, "backend.jsonl"),
            "{\"Timestamp\":\"2026-11-05T00:00:00+07:00\",\"Level\":\"Warning\",\"MessageTemplate\":\"jsonl line\"}\n",
            Encoding.UTF8);

        var result = LogReader.Read(_dir, "backend", null, "WARN", null, 10, LogFormat.Jsonl);

        Assert.True(result.Exists);
        Assert.Equal("backend.jsonl", result.File);
        Assert.Single(result.Lines);
        Assert.Contains("jsonl line", result.Lines[0]);
    }

    [Fact]
    public void Read_JsonlFormat_DoesNotReadTheTextFile()
    {
        File.WriteAllText(Path.Combine(_dir, "backend.log"), "2026-11-05 10:00:00.000 +07:00 [INF] text line\n", Encoding.UTF8);

        var result = LogReader.Read(_dir, "backend", null, null, null, 10, LogFormat.Jsonl);

        Assert.False(result.Exists);
    }
}
