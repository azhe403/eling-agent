using System.Globalization;
using System.Text.RegularExpressions;

namespace Eling.Core.Logging;

/// <summary>
/// Shared, read-only helper for reading Eling rolling log files. Used by both the
/// MCP <c>LogTools</c> and the dashboard REST endpoints so filtering/tail logic
/// stays in one place. Never throws for missing files; callers check results.
/// </summary>
public static class LogReader
{
    private static readonly Regex DateRegex = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly Regex JsonlTimestampRegex = new("\"Timestamp\":\"([^\"]+)\"", RegexOptions.Compiled);

    // One segment's read: the file and its filtered tail result.
    private readonly record struct SegmentRead(
        string Path,
        FilteredTailResult Read);

    // Sort key for one merged line: the parsed key (unparseable lines inherit
    // the previous parsed key in the same segment), then the segment index,
    // then the line's original index — the stable tie-break for a switch day.
    private readonly record struct LineSortKey(
        DateTimeOffset Key,
        int Segment,
        int Index);

    // A line paired with its sort key within the merged read.
    private readonly record struct KeyedLine(
        string Line,
        LineSortKey SortKey);

    private static readonly HashSet<string> AllowedStreams = new(StringComparer.OrdinalIgnoreCase) { "backend", "desktop" };

    /// <summary>
    /// The longest line a reader will hand back. A line longer than this is
    /// not a log line: it is a hole in the file, left by a process that
    /// truncated it while another was writing to it, and it can run to
    /// megabytes of NUL bytes. The real log line sits at the end of it.
    /// </summary>
    public const int MaxLineLength = 64 * 1024;

    /// <summary>
    /// The most bytes a single read may return. The MCP transport refuses a
    /// response past 16 MiB, so a read that ignores the size of its lines can
    /// take the whole tool down - and the lines here can be megabytes each.
    ///
    /// <para>
    /// The budget is deliberately well under that limit: JSON escapes a
    /// control character as six characters, so the serialized answer can be
    /// several times the text it carries.
    /// </para>
    /// </summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// A line that carries no readable text: the NUL padding of a hole in the
    /// file, or whitespace left where a process died mid-write.
    /// </summary>
    public static bool IsNoise(string line)
    {
        if (line.Length == 0) return true;
        foreach (var c in line)
        {
            if (c != '\0' && !char.IsWhiteSpace(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Trim a line to something a reader can use: drop the NUL padding of a
    /// hole in the file, and cut a line that is absurdly long.
    /// </summary>
    public static string Sanitize(string line)
    {
        var start = 0;
        while (start < line.Length && (line[start] == '\0' || char.IsWhiteSpace(line[start]))) start++;
        if (start > 0) line = line[start..];
        if (line.Length > MaxLineLength) line = line[..MaxLineLength];
        return line;
    }

    public static bool IsValidStream(string? stream) => !string.IsNullOrWhiteSpace(stream) && AllowedStreams.Contains(stream.Trim());

    public static bool IsValidDate(string? date) => string.IsNullOrWhiteSpace(date) || DateRegex.IsMatch(date.Trim());

    public static string ResolvePath(string logsDirectory, string stream, string? date)
        => ResolvePath(logsDirectory, stream, date, LogFormat.Text);

    /// <summary>
    /// Resolves the file for a stream, picking the extension from
    /// <paramref name="format"/>: text logs stay <c>.log</c>, JSONL switches to
    /// <c>.jsonl</c>.
    /// </summary>
    public static string ResolvePath(string logsDirectory, string stream, string? date, LogFormat format)
    {
        var s = stream.Trim();
        var extension = format == LogFormat.Jsonl ? ".jsonl" : ".log";
        var fileName = string.IsNullOrWhiteSpace(date) ? $"{s}{extension}" : $"{s}-{date.Trim()}{extension}";
        return Path.Combine(logsDirectory, fileName);
    }

    public static IReadOnlyList<LogFileInfo> ListFiles(string logsDirectory, string? streamFilter)
    {
        if (!Directory.Exists(logsDirectory)) return Array.Empty<LogFileInfo>();

        return Directory.GetFiles(logsDirectory, "*.log")
            .Concat(Directory.GetFiles(logsDirectory, "*.jsonl"))
            .Select(p => new FileInfo(p))
            .Where(f => string.IsNullOrWhiteSpace(streamFilter)
                || f.Name.StartsWith(streamFilter.Trim() + ".", StringComparison.OrdinalIgnoreCase)
                || f.Name.StartsWith(streamFilter.Trim() + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new LogFileInfo(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Read a tailed, filtered view using the Text format. Forwards to the
    /// <see cref="LogFormat"/> overload.
    /// </summary>
    public static LogReadResult Read(string logsDirectory, string stream, string? date, string? level, string? contains, int tail)
        => Read(logsDirectory, stream, date, level, contains, tail, LogFormat.Text);

    /// <summary>
    /// Read a tailed, filtered view of the log file(s) for a stream, resolving
    /// extensions from <paramref name="format"/>: text logs stay <c>.log</c>,
    /// JSONL switches to <c>.jsonl</c>. A read gathers every segment it can find
    /// — a dated read takes both <c>{stream}-{date}.log</c> and
    /// <c>{stream}-{date}.jsonl</c>; an active read takes today's archives plus
    /// the active file for the current format (the active file is the newest
    /// segment) — merges them per line by timestamp, and tails/sums as one
    /// sequence.
    /// </summary>
    /// <remarks>
    /// A switch mid-day parks half the day in each extension, so reading only
    /// the configured extension loses data. Segments are merged with a stable
    /// per-line sort; lines whose timestamp fails to parse inherit the previous
    /// parsed key in their segment (or <see cref="DateTimeOffset.MinValue"/>
    /// when none exists), keeping each segment's write order.
    /// </remarks>
    public static LogReadResult Read(
        string logsDirectory,
        string stream,
        string? date,
        string? level,
        string? contains,
        int tail,
        LogFormat format)
    {
        tail = Math.Clamp(tail, 1, 5000);
        var segments = Segments(logsDirectory, stream, date, format);
        if (segments.Count == 0)
        {
            var missing = ResolvePath(logsDirectory, stream, date, format);
            return new LogReadResult(Path.GetFileName(missing)!, logsDirectory, false, 0, 0, 0, Array.Empty<string>());
        }

        // Per segment: totals are summed, and at most `tail` lines are kept.
        var parts = segments
            .Select(p => new SegmentRead(p, ReadFilteredTail(p, level, contains, tail)))
            .ToList();

        var keyed = new List<KeyedLine>();
        for (var seg = 0; seg < parts.Count; seg++)
        {
            DateTimeOffset? previousKey = null;
            var lines = parts[seg].Read.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                keyed.Add(new KeyedLine(lines[i], SortKey(lines[i], seg, i, ref previousKey)));
            }
        }

        var merged = keyed
            .OrderBy(t => t.SortKey.Key)
            .ThenBy(t => t.SortKey.Segment)
            .ThenBy(t => t.SortKey.Index)
            .Select(t => t.Line)
            .ToList();

        // The per-segment budget caps each file individually, so the merged
        // list could outgrow what a transport can carry. Re-apply the same
        // MaxResponseBytes cap to the merged selection: drop the oldest
        // matching lines, keep the newest (mirrors ReadFilteredTail's byte
        // accounting). Totals stay summed; Returned shrinks instead.
        var selected = new List<string>();
        var bytes = 0;
        for (var i = merged.Count - 1; i >= 0 && selected.Count < tail; i--)
        {
            var len = merged[i].Length;
            if (bytes + len > MaxResponseBytes) break;
            bytes += len;
            selected.Add(merged[i]);
        }
        selected.Reverse();

        return new LogReadResult(
            Path.GetFileName(segments[0])!,
            logsDirectory,
            true,
            parts.Sum(p => p.Read.Total),
            parts.Sum(p => p.Read.Filtered),
            selected.Count,
            selected,
            segments.Select(p => Path.GetFileName(p)!).ToList());
    }

    // Every segment a read can see, in segment order: dated reads take the two
    // archives for the date; active reads take today's archives (a switch today
    // parks the stale active as an archive) plus the active file for the current
    // format — the active file last, as the newest segment.
    private static List<string> Segments(string logsDirectory, string stream, string? date, LogFormat format)
    {
        var s = stream.Trim();
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(date))
        {
            var d = date.Trim();
            candidates.Add(Path.Combine(logsDirectory, $"{s}-{d}.log"));
            candidates.Add(Path.Combine(logsDirectory, $"{s}-{d}.jsonl"));
        }
        else
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            candidates.Add(Path.Combine(logsDirectory, $"{s}-{today}.log"));
            candidates.Add(Path.Combine(logsDirectory, $"{s}-{today}.jsonl"));
            candidates.Add(ResolvePath(logsDirectory, s, null, format));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(File.Exists)
            .Where(p => seen.Add(Path.GetFullPath(p)))
            .ToList();
    }

    // Sort key for one merged line: the parsed key (unparseable lines inherit
    // the previous parsed key in the same segment, or MinValue when none exists),
    // then the segment index, then the line's original index — the stable tie-break
    // that keeps text-before-jsonl write order on a switch day.
    private static LineSortKey SortKey(
        string line, int segment, int index, ref DateTimeOffset? previousKey)
    {
        if (TryParseSortKey(line, out var timestamp))
        {
            previousKey = timestamp;
            return new LineSortKey(timestamp, segment, index);
        }
        return new LineSortKey(previousKey ?? DateTimeOffset.MinValue, segment, index);
    }

    private static bool TryParseSortKey(string line, out DateTimeOffset timestamp)
    {
        // Branch on line kind first: a TEXT line whose message embeds the
        // substring "Timestamp":"..." (e.g. a payload logged via Serilog's
        // {Message:lj}) must key off its leading timestamp, not the embedded
        // value — JSONL lines carry the real "Timestamp" member, but text
        // lines must never be keyed by an embedded lookalike.
        var trimmedStart = line.TrimStart();
        if (trimmedStart.StartsWith('{'))
        {
            var json = JsonlTimestampRegex.Match(line);
            if (json.Success)
            {
                return DateTimeOffset.TryParse(
                    json.Groups[1].Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out timestamp);
            }

            // JSONL-shaped line without a parseable Timestamp: fall through
            // to the text path would be misleading, so report failure and
            // let the carry-forward rule keep the line in segment order.
            timestamp = default;
            return false;
        }

        // Text: the default output template leads with "yyyy-MM-dd HH:mm:ss.fff zzz"
        // (exactly 30 chars, e.g. "2026-11-05 10:00:00.000 +07:00").
        var prefix = line.Length > 30 ? line[..30] : line;
        return DateTimeOffset.TryParse(
            prefix,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out timestamp);
    }

    /// <summary>
    /// Read lines appended since <paramref name="position"/>. Returns new lines and updates position.
    /// Handles file shrink (rotation) by resetting position to 0.
    /// </summary>
    public static IReadOnlyList<string> ReadNewLines(string path, ref long position, string? level, string? contains)
    {
        var result = new List<string>();
        if (!File.Exists(path)) { position = 0; return result; }

        var info = new FileInfo(path);
        if (info.Length < position) position = 0; // rotated/truncated

        if (info.Length == position) return result;

        // Read the new bytes into a buffer so we can track the consumed position
        // exactly. Using StreamReader alone is unsafe here because its internal
        // buffer reads ahead of the logical line position, which would skip lines
        // on the next poll (spec section 4.3 step 2).
        long remaining = info.Length - position;
        var buffer = new byte[remaining];
        int read = 0;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.Seek(position, SeekOrigin.Begin);
            while (read < remaining)
            {
                int r = stream.Read(buffer, read, (int)(remaining - read));
                if (r <= 0) break;
                read += r;
            }
        }

        if (read == 0) return result;

        // Only emit complete lines (up to the last newline). Keep any trailing
        // partial bytes for the next poll by not advancing past them.
        int lastNewline = -1;
        for (int i = read - 1; i >= 0; i--)
        {
            if (buffer[i] == (byte)'\n') { lastNewline = i; break; }
        }
        if (lastNewline < 0) return result; // no complete line yet

        int consumeLen = lastNewline + 1;
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, consumeLen);
        var lines = text.Split('\n');
        // Drop the trailing empty element produced by the final '\n'.
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            if (line.EndsWith("\r")) line = line.Substring(0, line.Length - 1);

            // A hole in the file, left by a process that truncated it while
            // another was writing: megabytes of NUL padding, then the real
            // line at the end of it. Only the real line is worth emitting.
            if (IsNoise(line)) continue;
            line = Sanitize(line);

            if (Matches(line, level, contains)) result.Add(line);
        }

        position += consumeLen;
        return result;
    }

    public static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>
    /// Fast, bounded tail read for SSE initial backlog: reads at most <paramref name="maxBytes"/>
    /// from the end of the file, splits, filters, and returns the last <paramref name="tail"/>
    /// matching lines. Does not compute totals. Safe for large files.
    /// </summary>
    public static IReadOnlyList<string> ReadTailChunk(string path, string? level, string? contains, int tail, int maxBytes = 262144)
    {
        var result = new List<string>();
        if (!File.Exists(path)) return result;
        tail = Math.Clamp(tail, 1, 5000);
        maxBytes = Math.Clamp(maxBytes, 4096, 4 * 1024 * 1024);

        var info = new FileInfo(path);
        if (info.Length == 0) return result;

        long toRead = Math.Min(info.Length, maxBytes);
        long start = info.Length - toRead;
        var buffer = new byte[toRead];
        int read = 0;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.Seek(start, SeekOrigin.Begin);
            while (read < toRead)
            {
                int r = stream.Read(buffer, read, (int)(toRead - read));
                if (r <= 0) break;
                read += r;
            }
        }
        if (read == 0) return result;

        // If we started mid-file, drop the first (possibly partial) line.
        int firstNewline = -1;
        if (start > 0)
        {
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n') { firstNewline = i; break; }
            }
        }

        int begin = firstNewline >= 0 ? firstNewline + 1 : 0;
        var text = System.Text.Encoding.UTF8.GetString(buffer, begin, read - begin);
        var lines = text.Split('\n');
        var tailBuffer = new Queue<string>(tail + 1);
        foreach (var raw in lines)
        {
            var line = raw.EndsWith("\r") ? raw.Substring(0, raw.Length - 1) : raw;
            if (IsNoise(line)) continue;
            line = Sanitize(line);
            if (!Matches(line, level, contains)) continue;
            tailBuffer.Enqueue(line);
            if (tailBuffer.Count > tail) tailBuffer.Dequeue();
        }
        return tailBuffer.ToList();
    }

    private static FilteredTailResult ReadFilteredTail(string path, string? level, string? contains, int tail)
    {
        var tailBuffer = new Queue<string>(tail + 1);
        int total = 0;
        int filteredCount = 0;
        int bytes = 0;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                total++;

                // A line of nothing but NUL bytes is a hole in the file, not a
                // log line. Counting it would inflate the totals and, worse,
                // hand the reader megabytes of padding.
                if (IsNoise(line)) continue;

                line = Sanitize(line);
                if (!Matches(line, level, contains)) continue;
                filteredCount++;

                // The budget applies to the window of kept lines only, never
                // by stopping the scan: totals count the whole file. Enqueue
                // first, then drop the oldest kept lines (subtracting their
                // lengths) until the window fits both the count and the
                // per-file byte cap.
                bytes += line.Length;
                tailBuffer.Enqueue(line);
                while (bytes > MaxResponseBytes && tailBuffer.Count > 0)
                {
                    bytes -= tailBuffer.Dequeue().Length;
                }
                if (tailBuffer.Count > tail)
                {
                    bytes -= tailBuffer.Dequeue().Length;
                }
            }
        }

        return new FilteredTailResult(total, filteredCount, tailBuffer.ToList());
    }

    // Maps user-facing level names (and the short Serilog tokens actually written
    // to the rolling log files, e.g. [ERR]/[INF]) to the set of bracketed tokens
    // that should count as a match. Serilog writes short tokens ([DBG]/[INF]/[WRN]/
    // [ERR]/[FTL]), while callers/tests often pass the long names (DEBUG/INFO/...).
    //
    // JSONL lines are matched on the "Level" field instead: Serilog core's
    // JsonFormatter (verified on 4.4.0) emits plain field names with the full
    // level name, e.g. {"Timestamp":"...","Level":"Warning","MessageTemplate":"..."}.
    // Both spellings are kept in the same set so a caller never has to know
    // which format a file has.
    private static readonly Dictionary<string, string[]> LevelTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DEBUG"] = new[] { "[DBG]", "[DEBUG]", "\"Level\":\"Debug\"" },
        ["DBG"] = new[] { "[DBG]", "[DEBUG]", "\"Level\":\"Debug\"" },
        ["TRACE"] = new[] { "[TRC]", "[TRACE]", "[VRB]", "[VERBOSE]", "\"Level\":\"Verbose\"" },
        ["TRC"] = new[] { "[TRC]", "[TRACE]", "[VRB]", "[VERBOSE]", "\"Level\":\"Verbose\"" },
        ["VERBOSE"] = new[] { "[TRC]", "[TRACE]", "[VRB]", "[VERBOSE]", "\"Level\":\"Verbose\"" },
        ["VRB"] = new[] { "[TRC]", "[TRACE]", "[VRB]", "[VERBOSE]", "\"Level\":\"Verbose\"" },
        ["INFO"] = new[] { "[INF]", "[INFO]", "\"Level\":\"Information\"" },
        ["INF"] = new[] { "[INF]", "[INFO]", "\"Level\":\"Information\"" },
        ["WARN"] = new[] { "[WRN]", "[WARN]", "[WARNING]", "\"Level\":\"Warning\"" },
        ["WRN"] = new[] { "[WRN]", "[WARN]", "[WARNING]", "\"Level\":\"Warning\"" },
        ["WARNING"] = new[] { "[WRN]", "[WARN]", "[WARNING]", "\"Level\":\"Warning\"" },
        ["ERROR"] = new[] { "[ERR]", "[ERROR]", "\"Level\":\"Error\"" },
        ["ERR"] = new[] { "[ERR]", "[ERROR]", "\"Level\":\"Error\"" },
        ["FATAL"] = new[] { "[FTL]", "[FATAL]", "[CRT]", "[CRITICAL]", "\"Level\":\"Fatal\"" },
        ["FTL"] = new[] { "[FTL]", "[FATAL]", "[CRT]", "[CRITICAL]", "\"Level\":\"Fatal\"" },
        ["CRITICAL"] = new[] { "[FTL]", "[FATAL]", "[CRT]", "[CRITICAL]", "\"Level\":\"Fatal\"" },
        ["CRT"] = new[] { "[FTL]", "[FATAL]", "[CRT]", "[CRITICAL]", "\"Level\":\"Fatal\"" },
    };

    public static bool Matches(string line, string? level, string? contains)
    {
        if (!string.IsNullOrWhiteSpace(level))
        {
            var lvl = level.Trim().ToUpperInvariant();
            string[] tokens;
            if (!LevelTokens.TryGetValue(lvl, out var mapped))
            {
                tokens = new[] { $"[{lvl}]" };
            }
            else
            {
                tokens = mapped;
            }
            var matched = false;
            foreach (var tok in tokens)
            {
                if (line.Contains(tok, StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
            }
            if (!matched) return false;
        }
        if (!string.IsNullOrWhiteSpace(contains))
        {
            if (!line.Contains(contains, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
