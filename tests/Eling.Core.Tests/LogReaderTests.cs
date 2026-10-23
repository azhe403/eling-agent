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
}
