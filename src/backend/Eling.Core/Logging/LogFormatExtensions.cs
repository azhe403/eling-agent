namespace Eling.Core.Logging;

/// <summary>
/// Parsing for the <see cref="LogFormat"/> values that arrive from hand-edited
/// configuration, where the value is a string the user typed.
/// </summary>
public static class LogFormatExtensions
{
    /// <summary>
    /// Parses a raw format string (e.g. from config) into a
    /// <see cref="LogFormat"/>. Case-insensitive; null, whitespace, or unknown
    /// values fall back to <see cref="LogFormat.Text"/>, which is the behaviour
    /// every release before this setting existed had.
    /// </summary>
    public static LogFormat Resolve(string? raw) =>
        string.IsNullOrWhiteSpace(raw) || !Enum.TryParse<LogFormat>(raw.Trim(), ignoreCase: true, out var parsed)
            ? LogFormat.Text
            : parsed;
}
