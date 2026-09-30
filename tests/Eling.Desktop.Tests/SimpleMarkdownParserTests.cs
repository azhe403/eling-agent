using Eling.Desktop.Formatting;
using Eling.Desktop.Models;

namespace Eling.Desktop.Tests;

/// <summary>
/// Every chat message is rendered from the output of <see cref="SimpleMarkdownParser"/>,
/// so the fence splitting decides what the user sees as prose and what they see as
/// code. The parser is pure, so it is pinned here directly rather than through a view.
/// </summary>
public class SimpleMarkdownParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\n\n")]
    public void Parse_InputThatTrimsAway_ProducesNoBlocks(string input)
    {
        var blocks = SimpleMarkdownParser.Parse(input);

        Assert.Empty(blocks);
    }

    [Fact]
    public void Parse_ProseWithNoFence_ProducesOneTextBlock()
    {
        var blocks = SimpleMarkdownParser.Parse("hello world");

        var block = Assert.Single(blocks);
        Assert.Equal(MessageBlockType.Text, block.Type);
        Assert.Equal("hello world", block.Content);
        Assert.Null(block.Language);
    }

    [Fact]
    public void Parse_FencedBlock_CapturesTheLanguageTag()
    {
        var blocks = SimpleMarkdownParser.Parse("```csharp\nvar x = 1;\n```");

        var block = Assert.Single(blocks);
        Assert.Equal(MessageBlockType.Code, block.Type);
        Assert.Equal("var x = 1;", block.Content);
        Assert.Equal("csharp", block.Language);
        Assert.Equal("csharp", block.LanguageDisplay);
    }

    [Fact]
    public void Parse_FenceWithoutLanguage_StillYieldsACodeBlock()
    {
        var blocks = SimpleMarkdownParser.Parse("```\nplain\n```");

        var block = Assert.Single(blocks);
        Assert.Equal(MessageBlockType.Code, block.Type);
        Assert.Equal("plain", block.Content);
        // The regex hands back an empty match rather than null, so Language is ""
        // and not absent — the display name must still read as a code block.
        Assert.Equal("code", block.LanguageDisplay);
    }

    [Fact]
    public void Parse_TextOnBothSidesOfAFence_KeepsTheOriginalOrder()
    {
        var blocks = SimpleMarkdownParser.Parse("before\n```py\nx = 1\n```\nafter");

        Assert.Equal(3, blocks.Count);
        Assert.Equal(MessageBlockType.Text, blocks[0].Type);
        Assert.Equal("before", blocks[0].Content);
        Assert.Equal(MessageBlockType.Code, blocks[1].Type);
        Assert.Equal("x = 1", blocks[1].Content);
        Assert.Equal(MessageBlockType.Text, blocks[2].Type);
        Assert.Equal("after", blocks[2].Content);
    }

    [Fact]
    public void Parse_TwoFences_DoesNotEmitABlankBlockForTheGapBetween()
    {
        var blocks = SimpleMarkdownParser.Parse("```\nfirst\n```\n\n```\nsecond\n```");

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(MessageBlockType.Code, b.Type));
        Assert.Equal("first", blocks[0].Content);
        Assert.Equal("second", blocks[1].Content);
    }

    [Fact]
    public void Parse_LeadingProseThatTrimsToNothing_EmitsNoEmptyTextBlock()
    {
        // "\n" before the fence trims away; without this the parser would push a
        // blank text row into the conversation ahead of the code.
        var blocks = SimpleMarkdownParser.Parse("\n```\nbody\n```");

        var block = Assert.Single(blocks);
        Assert.Equal(MessageBlockType.Code, block.Type);
    }

    [Fact]
    public void Parse_ProseWithInlineBackticks_IsNotSplitOnThem()
    {
        // Only a three-backtick fence delimits. A single or double backtick inside
        // prose must stay in the text rather than starting a code block.
        var blocks = SimpleMarkdownParser.Parse("use `x` and ``y`` inline");

        var block = Assert.Single(blocks);
        Assert.Equal(MessageBlockType.Text, block.Type);
        Assert.Contains("`x`", block.Content);
        Assert.Contains("``y``", block.Content);
    }

    [Theory]
    [InlineData("CSHARP", "csharp")]
    [InlineData("CSharp", "csharp")]
    [InlineData("PYTHON", "python")]
    public void Parse_LanguageTag_IsLowercasedForDisplay(string configured, string expected)
    {
        var blocks = SimpleMarkdownParser.Parse($"```{configured}\nbody\n```");

        var block = Assert.Single(blocks);
        Assert.Equal(configured, block.Language);
        Assert.Equal(expected, block.LanguageDisplay);
    }

    [Fact]
    public void MessageBlock_IsCodeAndIsText_AreMutuallyExclusive()
    {
        var code = new MessageBlock(MessageBlockType.Code, "x", "cs");
        var text = new MessageBlock(MessageBlockType.Text, "x");

        Assert.True(code.IsCode);
        Assert.False(code.IsText);
        Assert.True(text.IsText);
        Assert.False(text.IsCode);
    }
}
