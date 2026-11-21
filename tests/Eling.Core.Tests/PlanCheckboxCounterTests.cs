namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Xunit;

public class PlanCheckboxCounterTests
{
    [Fact]
    public void Count_SimpleCheckboxes_ReturnsCorrectCounts()
    {
        var content = "- [ ] Task 1\n- [x] Task 2\n- [ ] Task 3";
        var counts = PlanCheckboxCounter.Count(content);
        
        Assert.Equal(3, counts.Total);
        Assert.Equal(1, counts.Done);
    }

    [Fact]
    public void Count_FencedCode_Excluded()
    {
        var content = "- [ ] Real task\n\n```csharp\n- [x] Fake task\n```\n\n- [x] Another real";
        var counts = PlanCheckboxCounter.Count(content);
        
        Assert.Equal(2, counts.Total);
        Assert.Equal(1, counts.Done);
    }

    [Fact]
    public void Count_PerSection_Works()
    {
        var content = "# Section 1\n\n- [ ] A\n- [x] B\n\n# Section 2\n\n- [ ] C";
        var chunkResult = HeadingChunker.Chunk(content);
        var perSection = PlanCheckboxCounter.CountPerSection(content, chunkResult.Sections);
        
        Assert.Equal(2, perSection.Count);
        var section1 = perSection["section-1"];
        Assert.Equal(2, section1.Total);
        Assert.Equal(1, section1.Done);
        var section2 = perSection["section-2"];
        Assert.Equal(1, section2.Total);
        Assert.Equal(0, section2.Done);
    }

    [Fact]
    public void Count_NoCheckboxes_ReturnsZero()
    {
        var content = "Just text\n\nNo checkboxes here";
        var counts = PlanCheckboxCounter.Count(content);
        
        Assert.Equal(0, counts.Total);
        Assert.Equal(0, counts.Done);
    }
}