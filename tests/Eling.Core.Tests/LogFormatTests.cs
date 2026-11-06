using Eling.Core.Logging;
using Xunit;

namespace Eling.Core.Tests;

public sealed class LogFormatTests
{
    [Theory]
    [InlineData(null, LogFormat.Text)]
    [InlineData("", LogFormat.Text)]
    [InlineData("text", LogFormat.Text)]
    [InlineData("TEXT", LogFormat.Text)]
    [InlineData("jsonl", LogFormat.Jsonl)]
    [InlineData("JSONL", LogFormat.Jsonl)]
    [InlineData("xml", LogFormat.Text)]   // unknown → Text
    [InlineData("  ", LogFormat.Text)]
    public void Resolve_FallsBackToText_ForNullOrUnknown(string? raw, LogFormat expected)
        => Assert.Equal(expected, LogFormatExtensions.Resolve(raw));
}
