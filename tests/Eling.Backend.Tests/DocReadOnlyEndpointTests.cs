using System.Reflection;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The unified docs backend surface: no promote, no offer, no read-only class.
/// </summary>
public class DocReadOnlyEndpointTests
{
    [Fact]
    public void McpToolNames_DocsAssembly_HaveNoPromoteOrOffer()
    {
        var names = typeof(DocsReadTool).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(McpServerToolTypeAttribute), inherit: false).Length > 0)
            .Where(t => t.Namespace == "Eling.Backend.Mcp.Tools" && t.Name.StartsWith("Docs"))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)
                .Cast<McpServerToolAttribute>()
                .Select(a => string.IsNullOrEmpty(a.Name) ? m.Name : a.Name))
            .ToList();

        Assert.Contains("doc_save", names);
        Assert.DoesNotContain("doc_promote", names);
        Assert.DoesNotContain("doc_offer_managed", names);
    }

    [Fact]
    public async Task DocSave_WritesContentMetadataAndTaggedIndexRow()
    {
        using var fixture = new DocsFixture();
        var tool = new DocsWriteTool(fixture.Service);
        var dto = await tool.SaveAsync("docs/specs/new.md", "# New\n\nBody", "spec", "specs");

        Assert.True(File.Exists(Path.Combine(fixture.Root, "docs/specs/new.md")));
        Assert.Contains(await fixture.Service.ListAsync(fixture.Root), item => item.Path == "docs/specs/new.md");
        Assert.Equal(DocsService.ResolveWorkspaceId(fixture.Root), dto.WorkspaceId);
    }

    [Fact]
    public async Task DocArchive_LeavesMarkdownFileAndKeepsAudit()
    {
        using var fixture = new DocsFixture();
        var tool = new DocsWriteTool(fixture.Service);
        var dto = await tool.SaveAsync("docs/specs/new.md", "# New\n\nBody", "spec", "specs");
        var before = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "docs/specs/new.md"));

        Assert.True(await fixture.Service.ArchiveDocumentAsync(DocId.Parse(dto.Id)));

        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(fixture.Root, "docs/specs/new.md")));
        Assert.Equal(2, (await fixture.Service.GetAuditAsync(dto.Id)).Count);
    }

    [Fact]
    public async Task DocListTool_ResolvesWorkspaceRootAndLiteralAll()
    {
        using var fixture = new DocsFixture();
        var write = new DocsWriteTool(fixture.Service);
        await write.SaveAsync("docs/specs/a.md", "# Alpha\n\nBody", "spec", "specs");

        var read = new DocsReadTool(fixture.Service);
        Assert.Single(await read.ListAsync(workspace: fixture.Root));
        Assert.Single(await read.ListAsync(workspace: "all"));
        Assert.Empty(await read.ListAsync(workspace: "/some/other/root"));
    }

    [Fact]
    public async Task DocListPage_ReturnsTotalAndRespectsPaging()
    {
        using var fixture = new DocsFixture();
        var write = new DocsWriteTool(fixture.Service);
        await write.SaveAsync("docs/specs/a.md", "# Alpha\n\nBody", "spec", "specs");
        await write.SaveAsync("docs/specs/b.md", "# Beta\n\nBody", "spec", "specs");

        var first = await fixture.Service.ListPageAsync([fixture.Root], limit: 1, offset: 0);
        var second = await fixture.Service.ListPageAsync([fixture.Root], limit: 1, offset: 1);
        Assert.Equal(2, first.Total);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].DocId, second.Items[0].DocId);
    }

    [Fact]
    public async Task DocListTool_UsesServerSortAndNeverReSorts()
    {
        using var fixture = new DocsFixture();
        var write = new DocsWriteTool(fixture.Service);
        await write.SaveAsync("docs/b.md", "# Beta\n\nBody", "spec", "specs");
        await write.SaveAsync("docs/a.md", "# Alpha\n\nBody", "spec", "specs");

        var read = new DocsReadTool(fixture.Service);
        var list = await read.ListAsync(sort: "name", order: "asc");
        Assert.Equal("Alpha", list[0].Title);
        var desc = await read.ListAsync(sort: "name", order: "desc");
        Assert.Equal("Beta", desc[0].Title);
    }

    [Fact]
    public async Task DocReadTool_WithMalformedId_RejectsItInsteadOfReturningNull()
    {
        using var fixture = new DocsFixture();
        var read = new DocsReadTool(fixture.Service);

        await Assert.ThrowsAsync<ArgumentException>(() => read.ReadAsync("not-a-ulid"));
    }

    [Fact]
    public async Task DocRelationTool_WithMalformedSourceId_RejectsIt()
    {
        using var fixture = new DocsFixture();
        var read = new DocsReadTool(fixture.Service);

        await Assert.ThrowsAsync<ArgumentException>(
            () => read.RelationAsync(sourceId: "not-a-ulid", targetId: "01j00000000000000000000000", kind: "references"));
    }

    private sealed class DocsFixture : IDisposable
    {
        private readonly string _dbPath;
        public string Root { get; }
        public DocsService Service { get; }

        public DocsFixture()
        {
            var key = Guid.NewGuid().ToString("N");
            Root = Path.Combine(Path.GetTempPath(), $"eling-backend-docs-{key}");
            Directory.CreateDirectory(Root);
            _dbPath = Path.Combine(Path.GetTempPath(), $"eling-backend-docs-{key}.db");
            Service = new DocsService(
                new SqliteDocsIndex(_dbPath),
                new SqliteDocsAuditStore(_dbPath + ".audit"),
                [Root]);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            try { File.Delete(_dbPath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            try { File.Delete(_dbPath + ".audit"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
    }
}
