using System.Text;
using Eling.Core.Logging;
using Xunit;

namespace Eling.Core.Tests;

/// <summary>
/// Cross-format reads: a read gathers every segment it can find (dated archives
/// of both extensions, or today's archives plus the active file), merges them
/// into one timestamp-ordered sequence, and tails/sums as a single sequence.
/// Text lines lead with <c>yyyy-MM-dd HH:mm:ss.fff zzz</c>; JSONL lines carry
/// Serilog core's plain <c>"Timestamp"</c> member (verified on 4.4.0).
/// </summary>
public sealed class LogReaderMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "eling-logreader-merge-" + Guid.NewGuid().ToString("N"));

    public LogReaderMergeTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex)
        {
            // Best effort cleanup, but never silent: surface the failure.
            Console.Error.WriteLine($"LogReaderMergeTests: cleanup of '{_dir}' failed: {ex}");
        }
    }

    private void WriteLog(string fileName, params string[] lines)
        => File.WriteAllText(Path.Combine(_dir, fileName), string.Join('\n', lines) + "\n", Encoding.UTF8);

    private static string TextLine(string time, string marker)
        => $"2026-11-05 {time} +07:00 [INF] {marker}";

    private static string JsonlLine(string time, string marker)
        => $"{{\"Timestamp\":\"2026-11-05T{time}.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"{marker}\"}}";

    [Fact]
    public void DatedRead_BothExtensions_MergesAndOrdersByTimestamp()
    {
        WriteLog("backend-2026-11-05.log",
            TextLine("09:00:00.000", "log-0900"),
            TextLine("12:00:00.000", "log-1200"));
        WriteLog("backend-2026-11-05.jsonl",
            JsonlLine("11:00:00", "jsonl-1100"),
            JsonlLine("13:00:00", "jsonl-1300"));

        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, null, 10, LogFormat.Jsonl);

        Assert.True(result.Exists);
        Assert.Equal("backend-2026-11-05.log", result.File); // File = first segment
        Assert.Equal(new[] { "backend-2026-11-05.log", "backend-2026-11-05.jsonl" }, result.Files);
        Assert.Equal(4, result.TotalLines);
        Assert.Equal(new[] { "log-0900", "jsonl-1100", "log-1200", "jsonl-1300" },
            result.Lines.Select(MarkerOf).ToArray());
    }

    [Fact]
    public void DatedRead_Contains_MatchesAcrossBothFormats()
    {
        WriteLog("backend-2026-11-05.log",
            TextLine("10:00:00.000", "needle-from-log"),
            TextLine("10:05:00.000", "haystack-only"));
        WriteLog("backend-2026-11-05.jsonl",
            JsonlLine("11:00:00", "needle-from-jsonl"),
            JsonlLine("11:05:00", "also-haystack"));

        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, "needle", 10, LogFormat.Text);

        Assert.True(result.Exists);
        Assert.Equal(2, result.Returned);
        Assert.Contains(result.Lines, l => l.Contains("needle-from-log"));
        Assert.Contains(result.Lines, l => l.Contains("needle-from-jsonl"));
    }

    [Fact]
    public void DatedRead_OnlyOneExtension_ReturnsIt()
    {
        WriteLog("backend-2026-11-05.log", TextLine("10:00:00.000", "only-text"));

        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, null, 10, LogFormat.Jsonl);

        Assert.True(result.Exists);
        Assert.Equal("backend-2026-11-05.log", result.File);
        Assert.Equal(new[] { "backend-2026-11-05.log" }, result.Files);
        Assert.Single(result.Lines);
    }

    [Fact]
    public void DatedRead_NeitherExtension_NotFound()
    {
        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, null, 10, LogFormat.Jsonl);

        Assert.False(result.Exists);
        Assert.Null(result.Files);
        // File stays the current-format preferred path, as before.
        Assert.Equal("backend-2026-11-05.jsonl", result.File);
    }

    [Fact]
    public void ActiveRead_Jsonl_MergesTodaysArchiveAndActiveFile()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
        WriteLog($"backend-{today}.log",
            $"{today} 08:00:00.000 +07:00 [INF] morning-archive-line");
        WriteLog("backend.jsonl",
            $"{{\"Timestamp\":\"{today}T14:00:00.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"afternoon-active-line\"}}");
        WriteLog("backend.log",
            $"{yesterday} 23:00:00.000 +07:00 [INF] stale-yesterday-line");

        var result = LogReader.Read(_dir, "backend", null, null, null, 10, LogFormat.Jsonl);

        Assert.True(result.Exists);
        Assert.Equal($"backend-{today}.log", result.File); // first segment = today's archive
        Assert.Equal(new[] { $"backend-{today}.log", "backend.jsonl" }, result.Files);
        Assert.Contains(result.Lines, l => l.Contains("morning-archive-line"));
        Assert.Contains(result.Lines, l => l.Contains("afternoon-active-line"));
        Assert.DoesNotContain(result.Lines, l => l.Contains("stale-yesterday-line"));
        Assert.True(
            result.Lines[0].Contains("morning-archive-line"),
            "morning archive line should sort before the afternoon active line");
    }

    [Fact]
    public void ActiveRead_Text_MergesTodaysArchiveAndActiveFile()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
        WriteLog($"backend-{today}.jsonl",
            $"{{\"Timestamp\":\"{today}T08:00:00.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"morning-archive-line\"}}");
        WriteLog("backend.log",
            $"{today} 14:00:00.000 +07:00 [INF] afternoon-active-line");
        WriteLog("backend.jsonl",
            $"{{\"Timestamp\":\"{yesterday}T23:00:00.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"stale-yesterday-line\"}}");

        var result = LogReader.Read(_dir, "backend", null, null, null, 10, LogFormat.Text);

        Assert.True(result.Exists);
        Assert.Equal($"backend-{today}.jsonl", result.File); // first segment = today's archive
        Assert.Equal(new[] { $"backend-{today}.jsonl", "backend.log" }, result.Files);
        Assert.Contains(result.Lines, l => l.Contains("morning-archive-line"));
        Assert.Contains(result.Lines, l => l.Contains("afternoon-active-line"));
        Assert.DoesNotContain(result.Lines, l => l.Contains("stale-yesterday-line"));
        Assert.True(
            result.Lines[0].Contains("morning-archive-line"),
            "morning archive line should sort before the afternoon active line");
    }

    [Fact]
    public void DatedRead_DoubleSwitchDay_InterleavedTimestampsOrderCorrectly()
    {
        // A switch day can park half the day in each extension; sorting is per line.
        WriteLog("backend-2026-11-05.log",
            TextLine("09:00:00.000", "log-0900"),
            TextLine("12:00:00.000", "log-1200"),
            TextLine("15:00:00.000", "log-1500"));
        WriteLog("backend-2026-11-05.jsonl",
            JsonlLine("10:00:00", "jsonl-1000"),
            JsonlLine("13:00:00", "jsonl-1300"));

        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, null, 10, LogFormat.Text);

        Assert.Equal(
            new[] { "log-0900", "jsonl-1000", "log-1200", "jsonl-1300", "log-1500" },
            result.Lines.Select(MarkerOf).ToArray());
    }

    [Fact]
    public void DatedRead_UnparseableTimestampLine_KeepsSegmentOrder()
    {
        // Unparseable lines inherit the previous parsed key in their segment
        // (or MinValue when none exists); the merge must not throw and must
        // keep each segment's relative order.
        WriteLog("backend-2026-11-05.log",
            "garbage line with no timestamp MARK_GARBAGE",
            TextLine("10:00:00.000", "MARK_A"),
            "another unparseable line MARK_B");
        WriteLog("backend-2026-11-05.jsonl",
            JsonlLine("09:30:00", "MARK_C"));

        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, null, 10, LogFormat.Jsonl);

        // garbage: MinValue (no prior parsed key in segment) -> first.
        // MARK_C: 09:30 -> second. MARK_A: 10:00 -> third.
        // MARK_B: inherits MARK_A's 10:00, same segment+order -> last.
        Assert.Equal(
            new[] { "MARK_GARBAGE", "MARK_C", "MARK_A", "MARK_B" },
            result.Lines.Select(MarkerOf).ToArray());
    }

    [Fact]
    public void DatedRead_TextLineWithEmbeddedJsonlTimestamp_SortsByLeadingTimestamp()
    {
        // A text line whose message embeds "Timestamp":"..." with a LATER date
        // must still key off its leading timestamp, not the embedded lookalike.
        WriteLog("backend-2026-11-05.log",
            "2026-11-05 10:00:00.000 +07:00 [INF] payload \"Timestamp\":\"2026-12-31T23:59:59.000+07:00\" more text EMBEDDED-MARKER",
            TextLine("11:00:00.000", "PLAIN-LATER"));
        WriteLog("backend-2026-11-05.jsonl",
            JsonlLine("10:30:00", "JSONL-MID"));

        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, null, 10, LogFormat.Text);

        Assert.Equal(
            new[] { "EMBEDDED-MARKER", "JSONL-MID", "PLAIN-LATER" },
            result.Lines.Select(MarkerOf).ToArray());
    }

    [Fact]
    public void DatedRead_LargeFile_ReturnsEndAndCountsWholeFile()
    {
        // 25 lines of ~50 KB each: ~1.25 MB of matching text, more than the
        // per-file MaxResponseBytes window. The read must scan to EOF
        // (totals cover the whole file) and hand back the END of the file,
        // not the end of the first MiB.
        var lines = Enumerable.Range(0, 25)
            .Select(i => $"2026-11-01 00:00:{i:D2}.000 +07:00 [INF] {new string('x', 50000)} MARKER-{i:D2}")
            .ToArray();
        WriteLog("backend-2026-11-01.log", lines);

        var result = LogReader.Read(_dir, "backend", "2026-11-01", null, null, 3);

        Assert.True(result.Exists);
        Assert.Equal(25, result.TotalLines);
        Assert.Equal(25, result.FilteredTotal);
        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(new[] { "MARKER-22", "MARKER-23", "MARKER-24" },
            result.Lines.Select(l => l[^9..]).ToArray());
    }

    [Fact]
    public void DatedRead_MergedOutput_IsRecappedToMaxResponseBytes()
    {
        // Two segments each just under the per-file budget would otherwise
        // hand back ~2x MaxResponseBytes. Lines are sized so the merged set
        // is ~1.2 MB: the oldest matching lines must be dropped until the
        // merged selection fits the same 1 MiB cap (Returned shrinks).
        var logLines = Enumerable.Range(0, 10)
            .Select(i => BigLine($"08:{i:00}:00.000", $"log-{i:00}"))
            .ToArray();
        var jsonlLines = Enumerable.Range(0, 10)
            .Select(i => BigJsonlLine($"09:{i:00}:00", $"jsonl-{i:00}"))
            .ToArray();
        WriteLog("backend-2026-11-05.log", logLines);
        WriteLog("backend-2026-11-05.jsonl", jsonlLines);

        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, null, 100, LogFormat.Jsonl);

        Assert.Equal(20, result.TotalLines);
        Assert.Equal(20, result.FilteredTotal);
        Assert.True(result.Lines.Count < 20, "merged output must be re-capped below the per-segment keeps");
        Assert.True(result.Lines.Sum(l => (long)l.Length) <= LogReader.MaxResponseBytes,
            "merged selection must fit MaxResponseBytes");
        Assert.Equal(result.Lines.Count, result.Returned);
        Assert.DoesNotContain(result.Lines, l => l.Contains("log-00"));
        Assert.Contains(result.Lines, l => l.Contains("jsonl-09"));
    }

    [Fact]
    public void DatedRead_TailAppliesAcrossTheMerge()
    {
        WriteLog("backend-2026-11-05.log",
            TextLine("10:00:00.000", "log-a"),
            TextLine("10:01:00.000", "log-b"),
            TextLine("10:02:00.000", "log-c"),
            TextLine("10:03:00.000", "log-d"),
            TextLine("10:04:00.000", "log-e"));
        WriteLog("backend-2026-11-05.jsonl",
            JsonlLine("10:10:00", "jsonl-f"),
            JsonlLine("10:11:00", "jsonl-g"),
            JsonlLine("10:12:00", "jsonl-h"),
            JsonlLine("10:13:00", "jsonl-i"),
            JsonlLine("10:14:00", "jsonl-j"));

        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, null, 3, LogFormat.Jsonl);

        Assert.Equal(3, result.Returned);
        Assert.Equal(new[] { "jsonl-h", "jsonl-i", "jsonl-j" }, result.Lines.Select(MarkerOf).ToArray());
    }

    [Fact]
    public void DatedRead_TotalsAreSummedAcrossSegments()
    {
        WriteLog("backend-2026-11-05.log",
            TextLine("10:00:00.000", "needle-1"),
            TextLine("10:01:00.000", "needle-2"),
            TextLine("10:02:00.000", "other"));
        WriteLog("backend-2026-11-05.jsonl",
            JsonlLine("10:10:00", "needle-3"),
            JsonlLine("10:11:00", "other"));

        var result = LogReader.Read(_dir, "backend", "2026-11-05", null, "needle", 10, LogFormat.Text);

        Assert.Equal(5, result.TotalLines);
        Assert.Equal(3, result.FilteredTotal);
        Assert.Equal(3, result.Returned);
        Assert.Equal(3, result.Lines.Count);
    }

    private static string BigLine(string time, string marker)
    {
        var prefix = $"2026-11-05 {time} +07:00 [INF] {marker}-";
        return prefix + new string('x', 60000 - prefix.Length);
    }

    private static string BigJsonlLine(string time, string marker)
    {
        var prefix = $"{{\"Timestamp\":\"2026-11-05T{time}.000+07:00\",\"Level\":\"Information\",\"MessageTemplate\":\"{marker}-";
        return prefix + new string('x', 60000 - prefix.Length - 3) + "\"}";
    }

    // Text lines carry the marker as the last whitespace-separated token; JSONL
    // lines carry it in the "MessageTemplate" member (no spaces in the payload).
    private static string MarkerOf(string line)
    {
        const string key = "\"MessageTemplate\":\"";
        var start = line.LastIndexOf(key, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += key.Length;
            var end = line.IndexOf('"', start);
            return end >= 0 ? line[start..end] : line[start..];
        }
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
    }
}
