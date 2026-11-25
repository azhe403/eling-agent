namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Xunit;

public class DocsDomainTests
{
    private static Doc NewDoc(string workspaceId = "ws-a", string path = "docs/specs/a.md", bool archived = false)
        => new(
            DocId.NewId(), workspaceId, path,
            DocKind.Spec, "Test", DocStatusValue.DefaultFor(DocKind.Spec),
            "specs", 0, archived, null, 0, 0, "hash", DateTimeOffset.UtcNow);

    [Fact]
    public void DocId_NewId_IsUniqueAndOpaque()
    {
        var a = DocId.NewId();
        var b = DocId.NewId();
        Assert.NotEqual(a.Value, b.Value);
        Assert.DoesNotContain(":", a.Value);
        Assert.Equal(a.Value, DocId.Parse(a.Value).Value);
    }

    [Fact]
    public void Doc_CarriesWorkspacePathAndArchivedFlag()
    {
        var doc = NewDoc(workspaceId: "ws-a", path: "docs/specs/a.md", archived: true);
        Assert.Equal("ws-a", doc.WorkspaceId);
        Assert.Equal("docs/specs/a.md", doc.Path);
        Assert.True(doc.Archived);
    }

    [Fact]
    public void DocId_TryParse_HandlesInvalid()
    {
        Assert.False(DocId.TryParse(null, out _));
        Assert.False(DocId.TryParse("", out _));
        Assert.False(DocId.TryParse("invalid", out _));
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
        var doc = new Doc(
            DocId.NewId(), "ws-a", "test.md",
            DocKind.Plan, "Test", DocStatusValue.DefaultFor(DocKind.Plan),
            "topic", 0, false, null, 10, 3, "hash", DateTimeOffset.UtcNow);

        Assert.Equal(0.3, doc.Progress, 2);
    }
}
