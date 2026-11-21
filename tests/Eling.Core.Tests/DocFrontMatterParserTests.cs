namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Xunit;

public class DocFrontMatterParserTests
{
    [Fact]
    public void Parse_ValidFrontMatter_ReturnsParsedObject()
    {
        var content = @"---
kind: spec
topic: memory
order: 1
status: 1
supersededBy: other.md
---
Body content";
        
        var result = DocFrontMatterParser.Parse(content);
        
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.FrontMatter);
        Assert.Equal(DocKind.Spec, result.FrontMatter!.Kind);
        Assert.Equal("memory", result.FrontMatter.Topic);
        Assert.Equal(1, result.FrontMatter.Order);
        Assert.Equal(1, result.FrontMatter.Status);
        Assert.Equal("other.md", result.FrontMatter.SupersededBy);
        Assert.Equal("Body content", result.RemainingContent.Trim());
    }

    [Fact]
    public void Parse_NoFrontMatter_ReturnsNull()
    {
        var content = "# Heading\n\nJust content";
        var result = DocFrontMatterParser.Parse(content);
        
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.FrontMatter);
        Assert.Equal(content, result.RemainingContent);
    }

    [Fact]
    public void Parse_MalformedFrontMatter_ReturnsError()
    {
        var content = @"---
kind: invalid
status: not-a-number
---
Body";
        var result = DocFrontMatterParser.Parse(content);
        
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("parse error", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.FrontMatter);
        // Original content preserved
        Assert.Equal(content, result.RemainingContent);
    }

    [Fact]
    public void Parse_EmptyFrontMatter_ReturnsEmptyObject()
    {
        var content = @"---
---
Body";
        var result = DocFrontMatterParser.Parse(content);
        
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.FrontMatter);
        Assert.Equal("Body", result.RemainingContent.Trim());
    }
}