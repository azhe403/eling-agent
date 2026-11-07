using Eling.Core.Logging;
using Xunit;

namespace Eling.Core.Tests;

public sealed class LogReaderTests : IDisposable
{
    private readonly string _dir;

    public LogReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"eling-logreader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void IsValidStream_AcceptsKnown()
    {
        Assert.True(LogReader.IsValidStream("backend"));
        Assert.True(LogReader.IsValidStream("desktop"));
        Assert.False(LogReader.IsValidStream("foo"));
        Assert.False(LogReader.IsValidStream(null));
    }

    [Fact]
    public void IsValidDate_ValidatesFormat()
    {
        Assert.True(LogReader.IsValidDate(null));
        Assert.True(LogReader.IsValidDate(""));
        Assert.True(LogReader.IsValidDate("2026-10-23"));
        Assert.False(LogReader.IsValidDate("2026-10-23 extra"));
        Assert.False(LogReader.IsValidDate("not-a-date"));
    }

    [Fact]
    public void ListFiles_ReturnsEmptyForMissingDir()
    {
        var missing = Path.Combine(_dir, "nope");
        Assert.Empty(LogReader.ListFiles(missing, "backend"));
    }

    [Fact]
    public void ListFiles_FiltersByStream()
    {
        File.WriteAllText(Path.Combine(_dir, "backend.log"), "x");
        File.WriteAllText(Path.Combine(_dir, "desktop.log"), "x");
        var files = LogReader.ListFiles(_dir, "backend");
        Assert.Single(files);
        Assert.Equal("backend.log", files[0].Name);
    }

    [Fact]
    public void Read_MissingFile_ReturnsExistsFalse()
    {
        var r = LogReader.Read(_dir, "backend", "2020-01-01", null, null, 10);
        Assert.False(r.Exists);
        Assert.Equal(0, r.TotalLines);
    }

    [Fact]
    public void Read_TailsAndFilters()
    {
        var content = string.Join("\n", new[]
        {
            "2026-10-23 [INFO] hello",
            "2026-10-23 [ERROR] boom",
            "2026-10-23 [INFO] world",
        });
        File.WriteAllText(Path.Combine(_dir, "backend.log"), content);
        var r = LogReader.Read(_dir, "backend", null, "ERROR", null, 10);
        Assert.True(r.Exists);
        Assert.Equal(3, r.TotalLines);
        Assert.Equal(1, r.FilteredTotal);
        Assert.Equal(1, r.Returned);
        Assert.Contains("boom", r.Lines[0]);

        var r2 = LogReader.Read(_dir, "backend", null, null, null, 2);
        Assert.Equal(2, r2.Returned);
    }

    [Fact]
    public void ReadNewLines_TracksPositionAndHandlesShrink()
    {
        var path = Path.Combine(_dir, "backend.log");
        File.WriteAllText(path, "line1\nline2\n");
        long pos = 0;
        var first = LogReader.ReadNewLines(path, ref pos, null, null);
        Assert.Equal(2, first.Count);

        File.AppendAllText(path, "line3\n");
        var second = LogReader.ReadNewLines(path, ref pos, null, null);
        Assert.Single(second);
        Assert.Equal("line3", second[0]);

        // shrink (rotation)
        File.WriteAllText(path, "fresh\n");
        var third = LogReader.ReadNewLines(path, ref pos, null, null);
        Assert.Single(third);
        Assert.Equal("fresh", third[0]);
    }

    [Fact]
    public void Matches_FiltersLevelAndContains()
    {
        Assert.True(LogReader.Matches("2026 [ERROR] boom", "ERROR", "boom"));
        Assert.False(LogReader.Matches("2026 [INFO] hello", "ERROR", null));
        Assert.False(LogReader.Matches("2026 [INFO] hello", null, "boom"));
        Assert.True(LogReader.Matches("2026 [INFO] hello", null, null));
    }

    [Fact]
    public void AHoleInTheFile_IsNotALogLine()
    {
        // Several processes write to one log file, and a process that
        // truncates it while another is writing leaves a hole: megabytes of
        // NUL bytes, with the real line at the end of it. Handing that to a
        // reader would answer a question with megabytes of padding, and the
        // MCP transport would close the connection over it.
        var hole = new string('\0', 6_000_000) + "2026-11-07 [INF] real line";
        File.WriteAllText(Path.Combine(_dir, "backend.log"), hole + "\n2026-11-07 [ERR] after\n");

        var r = LogReader.Read(_dir, "backend", null, null, null, 10);

        Assert.Equal(2, r.Returned);
        Assert.DoesNotContain(r.Lines, l => l.Contains('\0'));
        Assert.All(r.Lines, l => Assert.True(l.Length < 100_000));
        Assert.Contains("real line", r.Lines[0]);
    }

    [Fact]
    public void ARead_IsBoundedByBytes_NotJustByLineCount()
    {
        // A tail count alone does not bound the answer: a few very long lines
        // can carry more than a transport will take.
        var line = new string('x', 200_000);
        var lines = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"{line}-{i}"));
        File.WriteAllText(Path.Combine(_dir, "backend.log"), lines);

        var r = LogReader.Read(_dir, "backend", null, null, null, 5000);

        Assert.True(r.Returned > 0);
        var bytes = r.Lines.Sum(l => l.Length);
        Assert.True(bytes <= LogReader.MaxResponseBytes, $"answer carried {bytes} bytes");
    }

    [Fact]
    public void IsNoise_RecognizesPaddingAndEmpty()
    {
        Assert.True(LogReader.IsNoise(""));
        Assert.True(LogReader.IsNoise(new string('\0', 100)));
        Assert.True(LogReader.IsNoise("   "));
        Assert.False(LogReader.IsNoise("2026 [INF] hello"));
    }

    [Fact]
    public void Sanitize_StripsPaddingAndCutsAnOverlongLine()
    {
        Assert.Equal("hello", LogReader.Sanitize("\0\0  hello"));
        var long_ = new string('y', LogReader.MaxLineLength + 100);
        Assert.Equal(LogReader.MaxLineLength, LogReader.Sanitize(long_).Length);
    }
}
