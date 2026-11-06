namespace Eling.Core.Logging;

public enum LogFormat { Text, Jsonl }

public static class LogFormatExtensions
{
    /// <summary>
    /// Parses a raw format string (e.g. from config) into a <see cref="LogFormat"/>.
    /// Case-insensitive; null, whitespace, or unknown values fall back to <see cref="LogFormat.Text"/>.
    /// </summary>
    public static LogFormat Resolve(string? raw) =>
        string.IsNullOrWhiteSpace(raw) || !Enum.TryParse<LogFormat>(raw.Trim(), ignoreCase: true, out var parsed)
            ? LogFormat.Text
            : parsed;
}
