namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using Xunit;

public class SqliteDocsIndexTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDocsIndex _index;

    public SqliteDocsIndexTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eling-docs-index-{Guid.NewGuid():N}.db");
        _index = new SqliteDocsIndex(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }

    private static Doc NewDoc(string path, string workspaceId, DocKind kind = DocKind.Spec, string title = "Spec")
        => new(DocId.NewId(), workspaceId, path, kind, title,
            DocStatusValue.DefaultFor(kind), "specs", 0, false, null, 0, 0, "hash", DateTimeOffset.UtcNow);

    [Fact]
    public async Task IndexAsync_AndGetAsync_RoundTrip()
    {
        var doc = NewDoc("docs/specs/a.md", "ws-a");
        await _index.IndexAsync(doc, [], []);

        var loaded = await _index.GetAsync(doc.Id.Value);
        Assert.NotNull(loaded);
        Assert.Equal("ws-a", loaded!.WorkspaceId);
        Assert.Equal("docs/specs/a.md", loaded.Path);
    }

    [Fact]
    public async Task ListAsync_WithWorkspaceFilter_ReturnsOnlyThatWorkspace()
    {
        await _index.IndexAsync(NewDoc("docs/a.md", "ws-a", title: "Alpha"), [], []);
        await _index.IndexAsync(NewDoc("docs/b.md", "ws-b", title: "Beta"), [], []);

        var a = await _index.ListAsync("ws-a");
        Assert.Single(a);
        Assert.Equal("Alpha", a[0].Title);
    }

    [Fact]
    public async Task TwoWorkspacesWithSamePath_GetDifferentUlids()
    {
        var a = NewDoc("docs/specs/a.md", "ws-a");
        var b = NewDoc("docs/specs/a.md", "ws-b");
        Assert.NotEqual(a.Id.Value, b.Id.Value);
        await _index.IndexAsync(a, [], []);
        await _index.IndexAsync(b, [], []);
        Assert.Equal(2, (await _index.ListAsync(null)).Count);
    }

    [Fact]
    public async Task ListAsync_SortsServerSideByNameAscDefault_AndByOtherKeysWithOrder()
    {
        await _index.IndexAsync(NewDoc("docs/b.md", "ws-a", title: "Beta"), [], []);
        await _index.IndexAsync(NewDoc("docs/a.md", "ws-a", title: "Alpha"), [], []);

        var asc = await _index.ListAsync();
        Assert.Equal("Alpha", asc[0].Title);
        var desc = await _index.ListAsync(sort: "name", order: "desc");
        Assert.Equal("Beta", desc[0].Title);
    }

    [Fact]
    public async Task ListAsync_UnknownSortKey_FallsBackToNameAsc()
    {
        await _index.IndexAsync(NewDoc("docs/b.md", "ws-a", title: "Beta"), [], []);
        await _index.IndexAsync(NewDoc("docs/a.md", "ws-a", title: "Alpha"), [], []);

        var list = await _index.ListAsync(sort: "bogus");
        Assert.Equal("Alpha", list[0].Title);
    }

    [Fact]
    public async Task DeleteAsync_RemovesDocRow()
    {
        var doc = NewDoc("docs/a.md", "ws-a");
        await _index.IndexAsync(doc, [], []);
        await _index.DeleteAsync(doc.Id.Value);
        Assert.Null(await _index.GetAsync(doc.Id.Value));
    }
}
