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
    private readonly DocsMetadataStore _metadata;
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
        _metadata = new DocsMetadataStore();
        _service = new DocsService(_index, _audit, _metadata, [_rootA, _rootB]);
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
    public async Task Scan_IndexesMarkdownFromAllRegisteredWorkspaces()
    {
        Write(_rootA, "docs/specs/a.md", "# Alpha\n\nBody A");
        Write(_rootB, "docs/specs/b.md", "# Beta\n\nBody B");
        await _service.ScanAsync();
        Assert.Equal(2, (await _service.ListAsync()).Count);
    }

    [Fact]
    public async Task Scan_UsesExistingUlidForSamePathAndAssignsNewForUnseenPath()
    {
        Write(_rootA, "docs/specs/a.md", "# Alpha\n\nBody A");
        await _service.ScanAsync();
        var first = (await _service.ListAsync()).Single().DocId;
        await _service.ScanAsync();
        Assert.Equal(first, (await _service.ListAsync()).Single().DocId);
        Write(_rootA, "docs/specs/b.md", "# Beta\n\nBody B");
        await _service.ScanAsync();
        Assert.Equal(2, (await _service.ListAsync()).Count);
    }

    [Fact]
    public async Task Scan_ImportsLegacyFrontMatterStatusOnce_AndNeverRewritesTheMarkdown()
    {
        var original = "---\nstatus: 2\n---\n# Approved spec\n\nBody";
        Write(_rootA, "docs/specs/approved.md", original);
        await _service.ScanAsync();
        var doc = (await _service.ListAsync()).Single();
        Assert.Equal(2, doc.Status);
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
    public async Task Scan_WithMalformedMetadataEntry_StillIndexesOtherDocs()
    {
        Write(_rootA, "docs/specs/a.md", "# Alpha\n\nBody");
        await File.WriteAllTextAsync(Path.Combine(_rootA, DocsMetadataStore.FileName), "{bad json");
        await _service.ScanAsync();
        Assert.Single(await _service.ListAsync());
    }

    [Fact]
    public async Task Create_WritesMarkdownAndJsonEntryAndIndexesOneTaggedRow()
    {
        var doc = await _service.CreateDocumentAsync(_rootA, "docs/specs/new.md", "# New\n\nBody", DocKind.Spec);
        Assert.True(File.Exists(Path.Combine(_rootA, "docs/specs/new.md")));
        var document = await _metadata.LoadAsync(_rootA);
        Assert.True(document.Docs.ContainsKey("docs/specs/new.md"));
        Assert.Equal(DocsMetadataStore.ResolveWorkspaceId(_rootA), (await _index.GetAsync(doc.Id.Value))!.WorkspaceId);
    }

    [Fact]
    public async Task Archive_SetsArchivedInJson_RemovesIndexRow_LeavesMarkdownFileUntouched()
    {
        var doc = await _service.CreateDocumentAsync(_rootA, "docs/specs/new.md", "# New\n\nBody", DocKind.Spec);
        var before = await File.ReadAllTextAsync(Path.Combine(_rootA, "docs/specs/new.md"));
        Assert.True(await _service.ArchiveDocumentAsync(doc.Id));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_rootA, "docs/specs/new.md")));
        Assert.Null(await _index.GetAsync(doc.Id.Value));
        var document = await _metadata.LoadAsync(_rootA);
        Assert.True(document.Docs["docs/specs/new.md"].Archived);
        Assert.Equal(2, (await _service.GetAuditAsync(doc.Id.Value)).Count);
    }

    [Fact]
    public async Task Annotate_WritesIntoJson_NotIntoTheMarkdownFile()
    {
        var doc = await _service.CreateDocumentAsync(_rootA, "docs/specs/new.md", "# New\n\n## Section\n\nBody", DocKind.Spec);
        var before = await File.ReadAllTextAsync(Path.Combine(_rootA, "docs/specs/new.md"));
        Assert.True(await _service.AnnotateSectionAsync(doc.Id, "section", 1, "note"));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_rootA, "docs/specs/new.md")));
        var document = await _metadata.LoadAsync(_rootA);
        Assert.Equal("note", document.Docs["docs/specs/new.md"].Annotations["section"].Text);
    }
}
