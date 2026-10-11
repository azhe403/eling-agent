using Eling.Desktop.Formatting;
using Eling.Desktop.Models;

namespace Eling.Desktop.Tests;

public sealed class ToolOutputFormatterTests
{
    [Theory]
    [InlineData("error: boom", true)]
    [InlineData("ERROR: boom", true)]
    [InlineData("  error: boom", true)]
    [InlineData("no error here", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsError_Matrix(string? output, bool expected)
    {
        // Act
        var actual = ToolOutputFormatter.IsError(output);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void FormatArguments_Empty_ReturnsEmpty(string? json, string expected)
    {
        // Act
        var actual = ToolOutputFormatter.FormatArguments(json);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FormatArguments_Object_RendersPairs()
    {
        // Act
        var actual = ToolOutputFormatter.FormatArguments("""{"path": "a.cs", "count": 3}""");

        // Assert
        Assert.Equal("path = a.cs, count = 3", actual);
    }

    [Fact]
    public void FormatArguments_NonObject_TruncatesRaw()
    {
        // Act
        var actual = ToolOutputFormatter.FormatArguments("[1, 2]");

        // Assert
        Assert.Equal("[1, 2]", actual);
    }

    [Fact]
    public void FormatArguments_InvalidJson_TruncatesRaw()
    {
        // Act
        var actual = ToolOutputFormatter.FormatArguments("{oops");

        // Assert
        Assert.Equal("{oops", actual);
    }

    [Fact]
    public void FormatArguments_LongNestedValue_TruncatesWithEllipsis()
    {
        // Arrange — nested object raw text exceeds the 60-char cut
        var json = "{\"key\": {\"nested\": \"" + new string('x', 61) + "\"}}";

        // Act
        var actual = ToolOutputFormatter.FormatArguments(json);

        // Assert
        Assert.StartsWith("key = ", actual);
        Assert.EndsWith("…", actual);
    }

    [Fact]
    public void BuildRawBlocks_Empty_ReturnsNone()
    {
        // Act
        var actual = ToolOutputFormatter.BuildRawBlocks("   ");

        // Assert
        Assert.Empty(actual);
    }

    [Fact]
    public void BuildRawBlocks_Error_ReturnsTextBlock()
    {
        // Act
        var actual = ToolOutputFormatter.BuildRawBlocks("error: boom");

        // Assert
        var block = Assert.Single(actual);
        Assert.Equal(MessageBlockType.Text, block.Type);
        Assert.Equal("error: boom", block.Content);
    }

    [Fact]
    public void BuildRawBlocks_Json_ReturnsCodeBlock()
    {
        // Act
        var actual = ToolOutputFormatter.BuildRawBlocks("""{"a":1}""");

        // Assert
        var block = Assert.Single(actual);
        Assert.Equal(MessageBlockType.Code, block.Type);
        Assert.Equal("json", block.Language);
        Assert.Contains("\"a\": 1", block.Content);
    }

    [Fact]
    public void BuildRawBlocks_PlainText_ReturnsTextBlock()
    {
        // Act
        var actual = ToolOutputFormatter.BuildRawBlocks("just words");

        // Assert
        var block = Assert.Single(actual);
        Assert.Equal(MessageBlockType.Text, block.Type);
        Assert.Equal("just words", block.Content);
    }
}
