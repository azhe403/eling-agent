namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Xunit;

public class HeadingChunkerTests
{
    [Fact]
    public void Chunk_SimpleHeadings_CreatesSections()
    {
        var content = "# Heading 1\n\nContent 1\n\n## Heading 2\n\nContent 2";
        var result = HeadingChunker.Chunk(content);
        
        Assert.Empty(result.Preamble);
        Assert.Equal(2, result.Sections.Count);
        Assert.Equal("heading-1", result.Sections[0].Anchor);
        Assert.Equal("heading-2", result.Sections[1].Anchor);
        Assert.Equal(1, result.Sections[0].Level);
        Assert.Equal(2, result.Sections[1].Level);
    }

    [Fact]
    public void Chunk_WithPreamble_CapturesPreamble()
    {
        var content = "Preamble text\n\n# Heading 1\n\nContent";
        var result = HeadingChunker.Chunk(content);
        
        Assert.Equal("Preamble text", result.Preamble.Trim());
        Assert.Single(result.Sections);
    }

    [Fact]
    public void Chunk_DuplicateHeadings_DeduplicatesAnchors()
    {
        var content = "# Same\n\nContent 1\n\n# Same\n\nContent 2";
        var result = HeadingChunker.Chunk(content);
        
        Assert.Equal("same", result.Sections[0].Anchor);
        Assert.Equal("same-2", result.Sections[1].Anchor);
    }

    [Fact]
    public void Chunk_FencedCode_NotSplit()
    {
        var content = "# Heading\n\n```csharp\n# Not a heading\n```\n\nContent";
        var result = HeadingChunker.Chunk(content);
        
        Assert.Single(result.Sections);
        Assert.Equal("heading", result.Sections[0].Anchor);
    }

    [Fact]
    public void Chunk_NoHeadings_EntireDocIsPreamble()
    {
        var content = "Just some text\n\nMore text";
        var result = HeadingChunker.Chunk(content);
        
        Assert.Empty(result.Sections);
        Assert.Equal(content.Trim(), result.Preamble.Trim());
    }
}