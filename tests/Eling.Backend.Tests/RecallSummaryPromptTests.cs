using Eling.Backend.Judging;
using Eling.Core.Memory;

namespace Eling.Backend.Tests;

/// <summary>
/// Locks the recall-summary prompt contract: labelled input, paragraph output,
/// bounded count — the pure behavior a provider can be tested against.
/// </summary>
public sealed class RecallSummaryPromptTests
{
    [Fact]
    public void Build_LabelsItemsStartingAtM1_WithTypeAndContent()
    {
        var items = new List<Memory>
        {
            new(MemoryType.Preference, "prefer stable releases only"),
            new(MemoryType.Decision, "http bridge stays")
        };

        var prompt = RecallSummaryPrompt.Build(items);

        Assert.Contains("M1:", prompt);
        Assert.Contains("M2:", prompt);
        Assert.Contains("prefer stable releases only", prompt);
        Assert.Contains("Type: Preference", prompt);
    }

    [Fact]
    public void Parse_SplitsParagraphsOnBlankLines()
    {
        var raw = "First theme paragraph.\n\nSecond theme paragraph.\n\nThird.";

        var paragraphs = RecallSummaryPrompt.Parse(raw);

        Assert.Equal(["First theme paragraph.", "Second theme paragraph.", "Third."], paragraphs);
    }

    [Fact]
    public void Parse_CapsAtFiveParagraphs()
    {
        var raw = string.Join("\n\n", Enumerable.Range(1, 8).Select(i => $"paragraph {i}"));

        var paragraphs = RecallSummaryPrompt.Parse(raw);

        Assert.Equal(5, paragraphs.Count);
    }

    [Fact]
    public void Parse_LongParagraph_IsClipped()
    {
        var raw = new string('x', 1000);

        var paragraphs = RecallSummaryPrompt.Parse(raw);

        var single = Assert.Single(paragraphs);
        Assert.EndsWith("...", single);
        Assert.True(single.Length < 700);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    public void Parse_UnusableAnswer_ReturnsEmpty(string raw)
    {
        Assert.Empty(RecallSummaryPrompt.Parse(raw));
    }
}
