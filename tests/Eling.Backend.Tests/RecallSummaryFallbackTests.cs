using Eling.Backend.Judging;
using Eling.Core.Memory;

namespace Eling.Backend.Tests;

/// <summary>
/// The deterministic digest used when no judge is available: bounded,
/// typed, clipped — and never silent.
/// </summary>
public sealed class RecallSummaryFallbackTests
{
    [Fact]
    public void Build_NonEmpty_ProducesTypedFragments()
    {
        var items = new List<Memory>
        {
            new(MemoryType.Preference, "always run git hygiene"),
            new(MemoryType.Decision, "http bridge stays")
        };

        var paragraphs = RecallSummaryFallback.Build(items);

        var single = Assert.Single(paragraphs);
        Assert.Contains("[Preference] always run git hygiene", single);
        Assert.Contains("[Decision] http bridge stays", single);
    }

    [Fact]
    public void Build_MoreThanTenItems_CapsAtTenFragments()
    {
        var items = Enumerable.Range(0, 15).Select(i => new Memory(MemoryType.Fact, $"fact {i}")).ToList();

        var paragraphs = RecallSummaryFallback.Build(items);

        // Ten fragments grouped three per paragraph become four paragraphs; items
        // past the tenth never appear.
        Assert.Equal(4, paragraphs.Count);
        Assert.All(paragraphs, p => Assert.DoesNotContain("fact 10", p));
    }

    [Fact]
    public void Build_LongContent_IsClipped()
    {
        var items = new List<Memory> { new(MemoryType.Fact, new string('y', 500)) };

        var paragraphs = RecallSummaryFallback.Build(items);

        var single = Assert.Single(paragraphs);
        Assert.EndsWith("...", single);
    }

    [Fact]
    public void Build_Empty_ReturnsEmpty()
    {
        Assert.Empty(RecallSummaryFallback.Build([]));
    }
}
