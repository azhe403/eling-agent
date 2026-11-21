namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Eling.Core.Scope;
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