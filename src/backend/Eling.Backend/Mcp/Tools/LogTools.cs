using System.ComponentModel;
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

    public LogTools(string? logsDirectory = null, ILogger<LogTools>? logger = null)
    {
        _logsDirectory = !string.IsNullOrWhiteSpace(logsDirectory)
            ? logsDirectory!
            : CentralLogDirectory.Resolve();
        _logger = logger;
    }

    [McpServerTool(Name = "log_list"), Description("List available Eling log files (active + rotated archives) with sizes and last-write times. Read-only; never throws.")]
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

    [McpServerTool(Name = "log_read"), Description("Read recent entries from an Eling log file for diagnosis. Defaults to today's active backend log. Supports an archive date, a level filter, a substring filter, and a tail count. Read-only; never throws for missing files.")]
    public string ReadLog(
        [Description("Log stream: 'backend' (default) or 'desktop'.")] string? stream = "backend",
        [Description("Optional archive date 'YYYY-MM-DD'. Empty selects the active log file.")] string? date = null,
        [Description("Max lines to return from the end of the file, clamped [1, 5000].")] int tail = 200,
        [Description("Optional level filter: DEBUG, INFO, WARN, ERROR, or FATAL. Empty = all levels.")] string? level = null,
        [Description("Optional case-insensitive substring filter applied to each line.")] string? contains = null)
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

            var result = LogReader.Read(_logsDirectory, resolvedStream, date, level, contains, tail);
            if (!result.Exists)
            {
                return JsonSerializer.Serialize(
                    new { ok = false, error = $"Log file not found: {result.File}", code = "not_found", directory = _logsDirectory },
                    JsonOptions);
            }

            return JsonSerializer.Serialize(
                new { file = result.File, directory = result.Directory, totalLines = result.TotalLines, filteredTotal = result.FilteredTotal, returned = result.Returned, lines = result.Lines },
                JsonOptions);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "log_read failed");
            return ErrorJson(ex);
        }
    }

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
