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
    private static readonly HashSet<string> AllowedStreams = new(StringComparer.OrdinalIgnoreCase) { "backend", "desktop" };

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
    /// Read a tailed, filtered view of a single log file.
    /// </summary>
    public static LogReadResult Read(string logsDirectory, string stream, string? date, string? level, string? contains, int tail)
        => Read(logsDirectory, stream, date, level, contains, tail, LogFormat.Text);

    /// <summary>
    /// Read a tailed, filtered view of a single log file, resolving its
    /// extension from <paramref name="format"/>.
    /// </summary>
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
        var path = ResolvePath(logsDirectory, stream, date, format);
        if (!File.Exists(path))
        {
            return new LogReadResult(Path.GetFileName(path), logsDirectory, false, 0, 0, 0, Array.Empty<string>());
        }

        var read = ReadFilteredTail(path, level, contains, tail);
        var total = read.Total;
        var filtered = read.Filtered;
        var lines = read.Lines;
        return new LogReadResult(Path.GetFileName(path), logsDirectory, true, total, filtered, lines.Count, lines);
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
            if (string.IsNullOrEmpty(line)) continue;
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

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                total++;
                if (!Matches(line, level, contains)) continue;
                filteredCount++;
                tailBuffer.Enqueue(line);
                if (tailBuffer.Count > tail) tailBuffer.Dequeue();
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
