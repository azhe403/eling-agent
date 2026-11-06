namespace Eling.Core.Logging;

/// <summary>
/// The on-disk shape of the rolling log files: plain text template lines, or
/// one JSON object per line.
/// </summary>
public enum LogFormat
{
    /// <summary>The historical text template, <c>{Timestamp} [Level] ... {Message}</c>.</summary>
    Text,

    /// <summary>One JSON object per line, as <c>Serilog.Formatting.Json.JsonFormatter</c> writes it.</summary>
    Jsonl,
}
