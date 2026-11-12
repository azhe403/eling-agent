using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Core.Logging;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Diagnostic tools for reading the Eling rolling log files produced by the
/// rolling daily file sink. Read-only; never throws for missing files.
/// Auto-discovered by <c>WithToolsFromAssembly()</c>. Shares its read logic
/// with the dashboard REST endpoints via <see cref="LogReader"/>.
/// </summary>
[McpServerToolType]
public sealed class LogTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _logsDirectory;
    private readonly ILogger<LogTools>? _logger;
    private readonly Func<LogFormat>? _getFormat;

    public LogTools(string? logsDirectory = null, ILogger<LogTools>? logger = null, Func<LogFormat>? getFormat = null)
    {
        _logsDirectory = !string.IsNullOrWhiteSpace(logsDirectory)
            ? logsDirectory!
            : CentralLogDirectory.Resolve();
        _logger = logger;
        _getFormat = getFormat;
    }

    [McpServerTool(Name = "log_list"), Description("List available Eling log files (active + rotated archives) with sizes and last-write times. Files of both formats (.log and .jsonl) are listed. Read-only; never throws.")]
    public string ListLogs(
        [Description("Optional stream filter: 'backend', 'desktop', or empty for all streams.")] string? stream = null)
    {
        try
        {
            if (!LogReader.IsValidStream(stream ?? "backend") && !string.IsNullOrWhiteSpace(stream))
            {
                return ErrorJson(new ArgumentException($"Unknown stream '{stream}'."));
            }
            var effective = string.IsNullOrWhiteSpace(stream) ? null : stream.Trim();
            var files = LogReader.ListFiles(_logsDirectory, effective);
            return JsonSerializer.Serialize(new { directory = _logsDirectory, count = files.Count, files }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "log_list failed");
            return ErrorJson(ex);
        }
    }

    [McpServerTool(Name = "log_read"), Description("Reads the format configured in log.json unless format is given. Lines are raw (JSONL lines are full JSON object strings). Dated reads merge both formats. An undated read also merges today's archives (.log and .jsonl) with the active file. Read-only; never throws for missing files.")]
    public string ReadLog(
        [Description("Log stream: 'backend' (default) or 'desktop'.")] string? stream = "backend",
        [Description("Optional archive date 'YYYY-MM-DD'. Empty selects the active log file.")] string? date = null,
        [Description("Max lines to return from the end of the merged sequence, clamped [1, 5000].")] int tail = 200,
        [Description("Optional level filter: DEBUG, INFO, WARN, ERROR, or FATAL. Empty = all levels.")] string? level = null,
        [Description("Optional case-insensitive substring filter applied to each line.")] string? contains = null,
        [Description("Optional format override: 'text' or 'jsonl'. Empty or 'auto' uses the configured format (log.json). Dated reads always span both extensions; this selects which active file is live.")] string? format = null)
    {
        try
        {
            var resolvedStream = string.IsNullOrWhiteSpace(stream) ? "backend" : stream.Trim();
            if (!LogReader.IsValidStream(resolvedStream))
            {
                return ErrorJson(new ArgumentException($"Unknown stream '{resolvedStream}'."));
            }
            if (!LogReader.IsValidDate(date))
            {
                return ErrorJson(new ArgumentException($"Invalid date '{date}'. Use YYYY-MM-DD."));
            }

            // Empty or 'auto' follows the configured format (log.json); 'text'/'jsonl'
            // override it — but an explicit format never narrows a dated read, which
            // always spans both extensions.
            var requested = (format ?? "auto").Trim();
            LogFormat? explicitFormat = null;
            if (requested.Length == 0 || string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
            {
                explicitFormat = null;
            }
            else if (string.Equals(requested, "text", StringComparison.OrdinalIgnoreCase))
            {
                explicitFormat = LogFormat.Text;
            }
            else if (string.Equals(requested, "jsonl", StringComparison.OrdinalIgnoreCase))
            {
                explicitFormat = LogFormat.Jsonl;
            }
            else
            {
                return ErrorJson(new ArgumentException($"Unknown format '{format}'. Use 'text', 'jsonl', or 'auto'."));
            }

            var effective = explicitFormat ?? _getFormat?.Invoke() ?? LogFormat.Text;
            var formatName = effective.ToString().ToLowerInvariant();

            var result = LogReader.Read(_logsDirectory, resolvedStream, date, level, contains, tail, effective);
            if (!result.Exists)
            {
                return JsonSerializer.Serialize(
                    new { ok = false, error = $"Log file not found: {result.File}", code = "not_found", directory = _logsDirectory, format = formatName },
                    JsonOptions);
            }

            var json = JsonSerializer.Serialize(
                new { file = result.File, directory = result.Directory, totalLines = result.TotalLines, filteredTotal = result.FilteredTotal, returned = result.Returned, lines = result.Lines, format = formatName, files = result.Files },
                JsonOptions);

            // The transport refuses a response past 16 MiB, and it closes the
            // connection rather than answering when a tool exceeds it - the
            // caller loses the tool, not just the answer. The reader already
            // bounds the text; this bounds the serialized form, which can be
            // several times larger because JSON escapes control characters.
            if (Encoding.UTF8.GetByteCount(json) <= MaxResponseBytes)
            {
                return json;
            }

            return JsonSerializer.Serialize(
                new
                {
                    ok = false,
                    error = "The answer is too large to send. Narrow it with a level or contains filter, or a smaller tail.",
                    code = "too_large",
                    file = result.File,
                    totalLines = result.TotalLines,
                    filteredTotal = result.FilteredTotal,
                    format = formatName,
                    files = result.Files
                },
                JsonOptions);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "log_read failed");
            return ErrorJson(ex);
        }
    }

    /// <summary>
    /// The most bytes a tool response may carry. The transport's own limit is
    /// 16 MiB; this leaves room for the JSON envelope around the answer.
    /// </summary>
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    private static string ErrorJson(Exception ex)
    {
        var code = ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => "not_found",
            ArgumentException => "invalid_argument",
            _ => "internal_error"
        };
        return JsonSerializer.Serialize(new { ok = false, error = ex.Message, code }, JsonOptions);
    }
}
