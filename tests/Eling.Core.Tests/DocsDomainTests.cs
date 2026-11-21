namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Xunit;

public class DocsDomainTests
{
    [Fact]
    public void DocId_EncodesOwnershipOriginAndPathHash()
    {
        var id = DocId.Create(DocOwnership.Managed, DocOrigin.Current, "docs/specs/my-spec.md");
        
        Assert.Equal(DocOwnership.Managed, id.Ownership);
        Assert.Equal(DocOrigin.Current, id.Origin);
        Assert.NotEmpty(id.PathHash);
        Assert.StartsWith("managed:current:", id.Value);
    }

    [Fact]
    public void DocId_Parse_RoundTrips()
    {
        var original = DocId.Create(DocOwnership.External, DocOrigin.Ancestor, "docs/plans/plan-1.md");
        var parsed = DocId.Parse(original.Value);
        
        Assert.Equal(original.Ownership, parsed.Ownership);
        Assert.Equal(original.Origin, parsed.Origin);
        Assert.Equal(original.PathHash, parsed.PathHash);
    }

    [Fact]
    public void DocId_TryParse_HandlesInvalid()
    {
        Assert.False(DocId.TryParse(null, out _));
        Assert.False(DocId.TryParse("", out _));
        Assert.False(DocId.TryParse("invalid", out _));
        Assert.False(DocId.TryParse("managed:invalid:hash", out _));
    }

    [Fact]
    public void DocStatusValue_DefaultForKind()
    {
        Assert.Equal(SpecStatus.Draft, (SpecStatus)DocStatusValue.DefaultFor(DocKind.Spec).Value);
        Assert.Equal(PlanStatus.Active, (PlanStatus)DocStatusValue.DefaultFor(DocKind.Plan).Value);
        Assert.Equal(DocStatus.Active, (DocStatus)DocStatusValue.DefaultFor(DocKind.Doc).Value);
        Assert.Equal(ScratchStatus.Archived, (ScratchStatus)DocStatusValue.DefaultFor(DocKind.Scratch).Value);
    }

    [Fact]
    public void DocStatusValue_IsSuperseded_Works()
    {
        Assert.True(DocStatusValue.FromSpec(SpecStatus.Superseded).IsSuperseded());
        Assert.True(DocStatusValue.FromPlan(PlanStatus.Superseded).IsSuperseded());
        Assert.True(DocStatusValue.FromDoc(DocStatus.Superseded).IsSuperseded());
        Assert.False(DocStatusValue.FromScratch(ScratchStatus.Archived).IsSuperseded());
    }

    [Fact]
    public void Doc_Progress_CalculatesCorrectly()
    {
        var id = DocId.Create(DocOwnership.Managed, DocOrigin.Current, "test.md");
        var doc = new Doc(
            id, DocOwnership.Managed, DocOrigin.Current, "test.md",
            DocKind.Plan, "Test", DocStatusValue.DefaultFor(DocKind.Plan),
            "topic", 0, null, 10, 3, "hash", DateTimeOffset.UtcNow);
        
        Assert.Equal(0.3, doc.Progress, 2);
    }

    [Fact]
    public void Doc_External_Managed_Properties()
    {
        var id = DocId.Create(DocOwnership.External, DocOrigin.Current, "docs/spec.md");
        var doc = new Doc(
            id, DocOwnership.External, DocOrigin.Current, "docs/spec.md",
            DocKind.Spec, "Spec", DocStatusValue.DefaultFor(DocKind.Spec),
            "specs", 0, null, 0, 0, "hash", DateTimeOffset.UtcNow);
        
        Assert.True(doc.IsExternal);
        Assert.False(doc.IsManaged);
    }
}

public class HeadingChunkerTests
{
    [Fact]
    public void Chunk_SimpleHeadings_CreatesSections()
    {
        var content = "# Heading 1\n\nContent 1\n\n## Heading 2\n\nContent 2";
        var (sections, preamble) = HeadingChunker.Chunk(content);
        
        Assert.Empty(preamble);
        Assert.Equal(2, sections.Count);
        Assert.Equal("heading-1", sections[0].Anchor);
        Assert.Equal("heading-2", sections[1].Anchor);
        Assert.Equal(1, sections[0].Level);
        Assert.Equal(2, sections[1].Level);
    }

    [Fact]
    public void Chunk_WithPreamble_CapturesPreamble()
    {
        var content = "Preamble text\n\n# Heading 1\n\nContent";
        var (sections, preamble) = HeadingChunker.Chunk(content);
        
        Assert.Equal("Preamble text", preamble.Trim());
        Assert.Single(sections);
    }

    [Fact]
    public void Chunk_DuplicateHeadings_DeduplicatesAnchors()
    {
        var content = "# Same\n\nContent 1\n\n# Same\n\nContent 2";
        var (sections, _) = HeadingChunker.Chunk(content);
        
        Assert.Equal("same", sections[0].Anchor);
        Assert.Equal("same-2", sections[1].Anchor);
    }

    [Fact]
    public void Chunk_FencedCode_NotSplit()
    {
        var content = "# Heading\n\n```csharp\n# Not a heading\n```\n\nContent";
        var (sections, _) = HeadingChunker.Chunk(content);
        
        Assert.Single(sections);
        Assert.Equal("heading", sections[0].Anchor);
    }

    [Fact]
    public void Chunk_NoHeadings_EntireDocIsPreamble()
    {
        var content = "Just some text\n\nMore text";
        var (sections, preamble) = HeadingChunker.Chunk(content);
        
        Assert.Empty(sections);
        Assert.Equal(content.Trim(), preamble.Trim());
    }
}

public class PlanCheckboxCounterTests
{
    [Fact]
    public void Count_SimpleCheckboxes_ReturnsCorrectCounts()
    {
        var content = "- [ ] Task 1\n- [x] Task 2\n- [ ] Task 3";
        var (total, done) = PlanCheckboxCounter.Count(content);
        
        Assert.Equal(3, total);
        Assert.Equal(1, done);
    }

    [Fact]
    public void Count_FencedCode_Excluded()
    {
        var content = "- [ ] Real task\n\n```csharp\n- [x] Fake task\n```\n\n- [x] Another real";
        var (total, done) = PlanCheckboxCounter.Count(content);
        
        Assert.Equal(2, total);
        Assert.Equal(1, done);
    }

    [Fact]
    public void Count_PerSection_Works()
    {
        var content = "# Section 1\n\n- [ ] A\n- [x] B\n\n# Section 2\n\n- [ ] C";
        var (sections, _) = HeadingChunker.Chunk(content);
        var perSection = PlanCheckboxCounter.CountPerSection(content, sections);
        
        Assert.Equal(2, perSection.Count);
        Assert.Equal((2, 1), perSection["section-1"]);
        Assert.Equal((1, 0), perSection["section-2"]);
    }

    [Fact]
    public void Count_NoCheckboxes_ReturnsZero()
    {
        var content = "Just text\n\nNo checkboxes here";
        var (total, done) = PlanCheckboxCounter.Count(content);
        
        Assert.Equal(0, total);
        Assert.Equal(0, done);
    }
}

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
        
        var (fm, remaining, error) = DocFrontMatterParser.Parse(content);
        
        Assert.Null(error);
        Assert.NotNull(fm);
        Assert.Equal(DocKind.Spec, fm!.Kind);
        Assert.Equal("memory", fm.Topic);
        Assert.Equal(1, fm.Order);
        Assert.Equal(1, fm.Status);
        Assert.Equal("other.md", fm.SupersededBy);
        Assert.Equal("Body content", remaining.Trim());
    }

    [Fact]
    public void Parse_NoFrontMatter_ReturnsNull()
    {
        var content = "# Heading\n\nJust content";
        var (fm, remaining, error) = DocFrontMatterParser.Parse(content);
        
        Assert.Null(error);
        Assert.Null(fm);
        Assert.Equal(content, remaining);
    }

    [Fact]
    public void Parse_MalformedFrontMatter_ReturnsError()
    {
        var content = @"---
kind: invalid
status: not-a-number
---
Body";
        var (fm, remaining, error) = DocFrontMatterParser.Parse(content);
        
        Assert.NotNull(error);
        Assert.Contains("parse error", error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fm);
        // Original content preserved
        Assert.Equal(content, remaining);
    }

    [Fact]
    public void Parse_EmptyFrontMatter_ReturnsEmptyObject()
    {
        var content = @"---
---
Body";
        var (fm, remaining, error) = DocFrontMatterParser.Parse(content);
        
        Assert.Null(error);
        Assert.NotNull(fm);
        Assert.Equal("Body", remaining.Trim());
    }
}

public class ExternalDocInferenceTests
{
    [Fact]
    public void Infer_SpecsFolder_ReturnsSpecKind()
    {
        var (kind, topic, status, order) = ExternalDocInference.Infer("docs/specs/my-spec.md", "# Title");
        
        Assert.Equal(DocKind.Spec, kind);
        Assert.Equal("specs", topic);
    }

    [Fact]
    public void Infer_PlansFolder_ReturnsPlanKind()
    {
        var (kind, topic, status, order) = ExternalDocInference.Infer("docs/plans/my-plan.md", "# Title");
        
        Assert.Equal(DocKind.Plan, kind);
        Assert.Equal("plans", topic);
    }

    [Fact]
    public void Infer_ExplicitKind_OverridesFolder()
    {
        var (kind, _, _, _) = ExternalDocInference.Infer("docs/random/file.md", "# Title", "plan");
        
        Assert.Equal(DocKind.Plan, kind);
    }

    [Fact]
    public void Infer_FilenameOrder_ExtractsNumericPrefix()
    {
        var (_, _, _, order) = ExternalDocInference.Infer("docs/specs/03-third-spec.md", "# Title");
        
        Assert.Equal(3, order);
    }

    [Fact]
    public void Infer_NoNumericPrefix_ReturnsZeroOrder()
    {
        var (_, _, _, order) = ExternalDocInference.Infer("docs/specs/alpha-spec.md", "# Title");
        
        Assert.Equal(0, order);
    }
}