namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using Xunit;

/// <summary>
/// External documents are owned by their source tree, so every mutation is refused
/// with a distinct exception the REST and MCP layers can map to 409 doc_read_only.
/// A plain InvalidOperationException cannot carry that contract: callers would have
/// to match on message text, which changes with wording.
/// </summary>
public sealed class DocReadOnlyTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteDocsIndex _current;
    private readonly DocsService _service;
    private readonly Doc _external;
    private readonly Doc _managed;

    public DocReadOnlyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-doc-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _current = new SqliteDocsIndex(Path.Combine(_root, "current.db"));
        var shard = new SqliteDocsIndex(Path.Combine(_root, "shard.db"));
        var global = new SqliteDocsIndex(Path.Combine(_root, "global.db"));
        var resolver = new DocScopeResolver(_root);
        _service = new DocsService(_current, shard, global, resolver);

        var stamp = DateTimeOffset.UtcNow;
        _external = MakeDoc(DocOwnership.External, "docs/specs/external.md", DocKind.Spec, stamp);
        _managed = MakeDoc(DocOwnership.Managed, ".eling/docs/specs/managed.md", DocKind.Plan, stamp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Doc MakeDoc(DocOwnership ownership, string relPath, DocKind kind, DateTimeOffset stamp)
        => new(
            DocId.Create(ownership, DocOrigin.Current, relPath),
            ownership,
            DocOrigin.Current,
            relPath,
            kind,
            "Read-only contract",
            DocStatusValue.DefaultFor(kind),
            kind == DocKind.Plan ? "plans" : "specs",
            0,
            null,
            0,
            0,
            "hash",
            stamp);

    [Fact]
    public async Task Create_ExternalPath_ThrowsDocReadOnly()
    {
        await Assert.ThrowsAsync<DocReadOnlyException>(
            () => _service.CreateDocumentAsync("docs/specs/new.md", "content", DocKind.Spec));
    }

    [Fact]
    public async Task Promote_AlreadyExternal_ThrowsDocReadOnly()
    {
        await _current.IndexAsync(_external, [], []);

        await Assert.ThrowsAsync<DocReadOnlyException>(
            () => _service.PromoteDocumentAsync(_external.Id.Value));
    }

    [Fact]
    public async Task Update_ExternalDoc_ThrowsDocReadOnly()
    {
        await _current.IndexAsync(_external, [], []);

        await Assert.ThrowsAsync<DocReadOnlyException>(
            () => _service.UpdateDocumentAsync(_external.Id.Value, "rewritten"));
    }

    [Fact]
    public async Task Archive_ExternalDoc_ThrowsDocReadOnly()
    {
        await _current.IndexAsync(_external, [], []);

        await Assert.ThrowsAsync<DocReadOnlyException>(
            () => _service.ArchiveDocumentAsync(_external.Id.Value));
    }

    [Fact]
    public async Task Annotate_ExternalDoc_ThrowsDocReadOnly()
    {
        await _current.IndexAsync(_external, [], []);

        await Assert.ThrowsAsync<DocReadOnlyException>(
            () => _service.AnnotateSectionAsync(_external.Id.Value, "section-1", 0, "note"));
    }

    [Fact]
    public async Task AddRelation_WithExternalEndpoint_ThrowsDocReadOnly()
    {
        await _current.IndexAsync(_managed, [], []);
        await _current.IndexAsync(_external, [], []);

        await Assert.ThrowsAsync<DocReadOnlyException>(
            () => _service.AddRelationAsync(_managed.Id.Value, _external.Id.Value, RelationKind.References));
    }
}
