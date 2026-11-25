namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using Xunit;

public class DocsServiceUnifiedTests : IDisposable
{
    private readonly string _rootA;
    private readonly string _rootB;
    private readonly string _dbPath;
    private readonly SqliteDocsIndex _index;
    private readonly SqliteDocsAuditStore _audit;
    private readonly DocsService _service;

    public DocsServiceUnifiedTests()
    {
        var key = Guid.NewGuid().ToString("N");
        _rootA = Path.Combine(Path.GetTempPath(), $"eling-docs-a-{key}");
        _rootB = Path.Combine(Path.GetTempPath(), $"eling-docs-b-{key}");
        Directory.CreateDirectory(Path.Combine(_rootA, "docs", "specs"));
        Directory.CreateDirectory(Path.Combine(_rootB, "docs", "specs"));
        _dbPath = Path.Combine(Path.GetTempPath(), $"eling-docs-u-{key}.db");
        _index = new SqliteDocsIndex(_dbPath);
        _audit = new SqliteDocsAuditStore(_dbPath + ".audit");
        _service = new DocsService(_index, _audit, [_rootA, _rootB]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootA, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        try { Directory.Delete(_rootB, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        try { File.Delete(_dbPath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        try { File.Delete(_dbPath + ".audit"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Constructor_UsesIndexAndAuditOnly()
    {
        var service = new DocsService(_index, _audit, [_rootA]);
        Assert.Contains(_rootA, service.WorkspaceRoots);
    }

    [Fact]
    public async Task Scan_IndexesMarkdownFromAllRegisteredWorkspaces()
    {
        Write(_rootA, "docs/specs/a.md", "# Alpha\n\nBody");
        Write(_rootB, "docs/specs/b.md", "# Beta\n\nBody");
        await _service.ScanAsync();
        Assert.Equal(2, (await _service.ListAsync()).Count);
    }

    [Fact]
    public async Task Scan_UsesExistingUlidForSamePathAndAssignsNewForUnseenPath()
    {
        Write(_rootA, "docs/specs/a.md", "# Alpha\n\nBody");
        await _service.ScanAsync();
        var first = Assert.Single(await _service.ListAsync()).DocId;
        await _service.ScanAsync();
        Assert.Equal(first, Assert.Single(await _service.ListAsync()).DocId);
        Write(_rootA, "docs/specs/b.md", "# Beta\n\nBody");
        await _service.ScanAsync();
        Assert.Equal(2, (await _service.ListAsync()).Count);
    }

    [Fact]
    public async Task Scan_ImportsLegacyFrontMatterStatusOnce_AndNeverRewritesTheMarkdown()
    {
        var original = "---\nstatus: 2\n---\n# Approved spec\n\nBody";
        Write(_rootA, "docs/specs/approved.md", original);
        await _service.ScanAsync();
        Assert.Equal(2, Assert.Single(await _service.ListAsync()).Status);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_rootA, "docs/specs/approved.md")));
    }

    [Fact]
    public async Task Search_WorkspaceAll_ReturnsDocsFromTwoWorkspacesWithoutIdCollision()
    {
        Write(_rootA, "docs/specs/a.md", "# Alpha\n\nsharedword");
        Write(_rootB, "docs/specs/b.md", "# Beta\n\nsharedword");
        await _service.ScanAsync();
        var all = await _service.SearchAsync("sharedword");
        Assert.Equal(2, all.Count);
        Assert.Equal(2, all.Select(r => r.DocId).Distinct().Count());
    }

    [Fact]
    public async Task List_SpecificWorkspaceRootAndLiteralAll_ResolveToCorrectRows()
    {
        Write(_rootA, "docs/specs/same.md", "# Alpha\n\nBody");
        Write(_rootB, "docs/specs/same.md", "# Beta\n\nBody");
        await _service.ScanAsync();
        Assert.Single(await _service.ListAsync(_rootA));
        Assert.Single(await _service.ListAsync(_rootB));
        Assert.Equal(2, (await _service.ListAsync("all")).Count);
    }

    [Fact]
    public async Task Relation_SurvivesRescan_AsUlidEdge()
    {
        var source = await _service.CreateDocumentAsync(_rootA, "docs/plans/p.md", "# Plan\n\nBody", DocKind.Plan);
        var target = await _service.CreateDocumentAsync(_rootA, "docs/specs/s.md", "# Spec\n\nBody", DocKind.Spec);
        await _service.AddRelationAsync(source.Id, target.Id, RelationKind.Implements);
        Assert.Single(await _index.GetRelationsAsync(source.Id.Value, true));
        await _service.ScanAsync(full: true);
        Assert.Single(await _index.GetRelationsAsync(source.Id.Value, true));
    }

    [Fact]
    public async Task Scan_ResolvesFrontMatterSpecPathToUlidRelation()
    {
        Write(_rootA, "docs/specs/target.md", "# Target\n\nBody");
        await _service.ScanAsync();
        var target = Assert.Single((await _service.ListAsync(_rootA)).Where(item => item.Path == "docs/specs/target.md"));

        Write(_rootA, "docs/plans/linked.md", "---\nspec: docs/specs/target.md\n---\n# Plan\n\nBody");
        await _service.ScanAsync();
        var plan = Assert.Single((await _service.ListAsync(_rootA)).Where(item => item.Path == "docs/plans/linked.md"));
        var relation = Assert.Single(await _index.GetRelationsAsync(plan.DocId, true));
        Assert.Equal(target.DocId, DocId.Parse(relation.TargetId.Value).Value);
    }

    [Fact]
    public async Task Create_WritesMarkdownAndIndexRow()
    {
        var doc = await _service.CreateDocumentAsync(_rootA, "docs/specs/new.md", "# New\n\nBody", DocKind.Spec);
        Assert.True(File.Exists(Path.Combine(_rootA, "docs/specs/new.md")));
        var row = await _index.GetAsync(doc.Id.Value);
        Assert.NotNull(row);
        Assert.Equal(DocsService.ResolveWorkspaceId(_rootA), row!.WorkspaceId);
    }

    [Fact]
    public async Task Archive_KeepsRowWithArchivedFlag_AndLeavesMarkdownFile()
    {
        var doc = await _service.CreateDocumentAsync(_rootA, "docs/specs/new.md", "# New\n\nBody", DocKind.Spec);
        var before = await File.ReadAllTextAsync(Path.Combine(_rootA, "docs/specs/new.md"));
        Assert.True(await _service.ArchiveDocumentAsync(doc.Id));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_rootA, "docs/specs/new.md")));
        var row = await _index.GetAsync(doc.Id.Value);
        Assert.NotNull(row);
        Assert.True(row!.Archived);
        Assert.Empty(await _service.ListAsync(_rootA));
        Assert.Equal(2, (await _service.GetAuditAsync(doc.Id.Value)).Count);
    }

    [Fact]
    public async Task Archive_ExcludesFromSearch()
    {
        var doc = await _service.CreateDocumentAsync(_rootA, "docs/specs/searchable.md", "# Searchable\n\nuniqueword", DocKind.Spec);
        Assert.Single(await _service.SearchAsync("uniqueword"));
        await _service.ArchiveDocumentAsync(doc.Id);
        Assert.Empty(await _service.SearchAsync("uniqueword"));
    }

    [Fact]
    public async Task Annotate_WritesSectionRow_NotIntoTheMarkdownFile()
    {
        var doc = await _service.CreateDocumentAsync(_rootA, "docs/specs/new.md", "# New\n\n## Section\n\nBody", DocKind.Spec);
        var before = await File.ReadAllTextAsync(Path.Combine(_rootA, "docs/specs/new.md"));
        Assert.True(await _service.AnnotateSectionAsync(doc.Id, "section", 1, "note"));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_rootA, "docs/specs/new.md")));
        var section = await _index.GetSectionAsync(doc.Id.Value, "section");
        Assert.NotNull(section);
        Assert.Equal("note", section!.Annotation);
    }

    [Fact]
    public async Task Scan_PrunesRowsForDeletedMarkdown()
    {
        var doc = await _service.CreateDocumentAsync(_rootA, "docs/specs/gone.md", "# Gone\n\nBody", DocKind.Spec);
        File.Delete(Path.Combine(_rootA, "docs/specs/gone.md"));
        await _service.ScanAsync();
        Assert.Null(await _index.GetAsync(doc.Id.Value));
    }
}
