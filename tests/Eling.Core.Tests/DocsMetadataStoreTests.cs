namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Xunit;

public class DocsMetadataStoreTests : IDisposable
{
    private readonly string _root;
    private readonly DocsMetadataStore _store = new();

    public DocsMetadataStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"eling-docs-meta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }

    [Fact]
    public async Task LoadSave_RoundTripsEntriesAndCoverage()
    {
        var document = new DocsMetadataDocument
        {
            Coverage = new CoveragePolicy { Include = ["docs/**"], Exclude = ["docs/tmp/**"] }
        };
        var entry = _store.GetOrCreate(document, "docs/specs/a.md", "ws-a");
        entry.Kind = DocKind.Spec;
        entry.Topic = "specs";
        entry.Archived = true;
        await _store.SaveAsync(_root, document);

        var loaded = await _store.LoadAsync(_root);
        var reloaded = loaded.Docs["docs/specs/a.md"];
        Assert.Equal(entry.Id, reloaded.Id);
        Assert.Equal("ws-a", reloaded.WorkspaceId);
        Assert.Equal(DocKind.Spec, reloaded.Kind);
        Assert.True(reloaded.Archived);
        Assert.Equal("docs/**", loaded.Coverage.Include[0]);
    }

    [Fact]
    public async Task Load_WithOneMalformedEntry_KeepsOtherEntries()
    {
        var json = """
            {"version":1,"docs":{"docs/specs/a.md":{"id":"01j","path":"docs/specs/a.md","workspaceId":"ws-a"},"bad":{"id":123}}}
            """;
        await File.WriteAllTextAsync(Path.Combine(_root, DocsMetadataStore.FileName), json);

        var loaded = await _store.LoadWithFindingsAsync(_root);
        var document = loaded.Document;
        var findings = loaded.Findings;
        Assert.NotEmpty(findings);
        Assert.True(document.Docs.ContainsKey("docs/specs/a.md"));
    }

    [Fact]
    public void GetOrCreate_ReusesIdForSamePath_AndCreatesDifferentIdsForOtherWorkspaces()
    {
        var document = new DocsMetadataDocument();
        var a = _store.GetOrCreate(document, "docs/a.md", "ws-a");
        var again = _store.GetOrCreate(document, "docs/a.md", "ws-b");
        Assert.Equal(a.Id, again.Id);
        Assert.NotEqual(a.Id, _store.GetOrCreate(document, "docs/b.md", "ws-a").Id);
    }

    [Fact]
    public async Task OutOfWorkspacePath_IsWrittenToLocalOverlay_NotToCommittedJson()
    {
        var document = new DocsMetadataDocument();
        var entry = _store.GetOrCreate(document, "abs:/tmp/note.md", "ws-a");
        Assert.Contains("abs:/tmp/note.md", document.Docs.Keys);
        await _store.SaveAsync(_root, document);

        var overlay = new DocsLocalMetadataOverlay(_root);
        var entries = await overlay.LoadAsync();
        entries[entry.Path] = entry;
        await overlay.SaveAsync(entries);

        Assert.True(File.Exists(Path.Combine(_root, ".eling", "docs-local-metadata.json")));
        Assert.DoesNotContain("/tmp/note.md", await File.ReadAllTextAsync(Path.Combine(_root, DocsMetadataStore.FileName)));
    }
}
