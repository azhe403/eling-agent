using System.Text.Json;
using Eling.Backend.Agent.Services.Tools;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Eling.Core.Codebase;
using Eling.Core.Scope;

namespace Eling.Backend.Tests;

/// <summary>
/// The MCP tool's file half: the query is matched against the indexed file
/// paths instead of the indexed contents. The two scopes differ in exactly
/// one thing — "path" sees the folders, "file" sees only the name — and that
/// difference is the whole point of the feature, so both are pinned here.
/// The class joins the ELING_DATA_DIR collection because the federated path
/// reads sibling indexes from the global store under that variable.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseSearchMatchTests : CodebaseSearchToolTestBase
{
    public CodebaseSearchMatchTests()
    {
        WriteAsync("src/backend/Widget.cs", "public sealed class ZephyrWidget { }\n").GetAwaiter().GetResult();
        WriteAsync("src/widget/WidgetHelper.cs", "public sealed class WidgetHelper { }\n").GetAwaiter().GetResult();
        IndexAsync(_workspace).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Files_Mode_Audit_Records_The_Returned_File_Count()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        // match="file" matches the query against the file NAME, so the
        // token has to live in the file name, not in the contents.
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, $"AuditProbe{tag}.cs"),
            $"class AuditProbe{tag} {{ }}\n");
        var svc = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
        await svc.IndexAsync(full: true);

        var auditRoot = Path.Combine(Path.GetTempPath(), "eling-match-audit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new AuditFileStore(auditRoot, TimeProvider.System);
            var auditIndex = new SqliteAuditIndex(auditRoot, store);
            await auditIndex.InitializeAsync(CancellationToken.None);
            var logger = new BufferedAuditLogger(store, auditIndex, new AuditOptions(), TimeProvider.System);
            var tool = new CodebaseSearchTool(svc, registry: null, logger: null, audit: logger);

            using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
            {
                await tool.SearchAsync("auditprobe" + tag, match: "file", cancellationToken: CancellationToken.None);
            }

            await logger.DisposeAsync();

            var dayFile = Path.Combine(auditRoot, "access", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
            var line = Assert.Single(File.ReadAllLines(dayFile));
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("codebase_search", doc.RootElement.GetProperty("action").GetString());
            Assert.Equal("auditprobe" + tag.ToLowerInvariant(), doc.RootElement.GetProperty("query").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("resultCount").GetInt32());
        }
        finally
        {
            CodebaseRebuildScopeTests.TryDelete(auditRoot);
        }
    }

    [Fact]
    public async Task Content_Mode_TotalHits_Is_Limit_Independent()
    {
        // One file per occurrence: chunks never span files, so ten files
        // with the marker give ten hits regardless of how the passes count.
        for (var i = 0; i < 10; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_workspace, "src", $"ZephyrNeedle{i}.cs"),
                "public sealed class ZephyrLimitNeedleToken { }\n");
        }
        await IndexAsync(_workspace);
        var tool = Tool();

        var small = await tool.SearchAsync("ZephyrLimitNeedleToken", limit: 1, scope: "project");
        var all = await tool.SearchAsync("ZephyrLimitNeedleToken", limit: 50, scope: "project");
        var globalSmall = await tool.SearchAsync("ZephyrLimitNeedleToken", limit: 1);
        var globalAll = await tool.SearchAsync("ZephyrLimitNeedleToken", limit: 50);

        Assert.Equal(all.Stats.TotalHits, small.Stats.TotalHits);
        Assert.Equal(globalAll.Stats.TotalHits, globalSmall.Stats.TotalHits);
        Assert.True(all.Stats.TotalHits >= 8, $"expected at least 8 total hits, got {all.Stats.TotalHits}");
    }

    [Fact]
    public async Task Agent_Tool_Forwards_The_Match_Parameter()
    {
        var agent = new CodebaseSearchAgentTool(Tool());

        var json = await agent.ExecuteAsync("{\"query\":\"widget\",\"match\":\"file\"}", CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("files", doc.RootElement.GetProperty("mode").GetString());
        Assert.NotEmpty(doc.RootElement.GetProperty("files").EnumerateArray());
    }


    [Fact]
    public async Task File_Mode_Finds_A_File_By_Name()
    {
        var tool = Tool();

        var response = await tool.SearchAsync("widget", match: "file");

        Assert.Equal("files", response.Mode);
        Assert.Empty(response.Hits);
        Assert.Contains(response.Files!, f => f.Path == "src/backend/Widget.cs");
    }

    [Fact]
    public async Task Path_Mode_Finds_Files_Under_A_Folder_Name()
    {
        var tool = Tool();

        var response = await tool.SearchAsync("backend", match: "path");

        Assert.Equal("files", response.Mode);
        var file = Assert.Single(response.Files!);
        Assert.Equal("src/backend/Widget.cs", file.Path);
    }

    [Fact]
    public async Task File_Mode_Ignores_A_Folder_Name_In_The_Path()
    {
        var tool = Tool();

        var response = await tool.SearchAsync("backend", match: "file");

        Assert.Equal("files", response.Mode);
        Assert.Empty(response.Files!);
        Assert.NotNull(response.Stats.Hint);
    }

    [Fact]
    public async Task Match_Is_Case_And_Whitespace_Insensitive()
    {
        var tool = Tool();

        var response = await tool.SearchAsync("widget", match: " FILE ");

        Assert.Equal("files", response.Mode);
    }

    [Fact]
    public async Task An_Unknown_Match_Returns_A_Hint_With_Zero_Hits()
    {
        var tool = Tool();

        var response = await tool.SearchAsync("widget", match: "name");

        Assert.Equal("content", response.Mode);
        Assert.Equal(0, response.Stats.TotalHits);
        Assert.Equal("Invalid match 'name'. Valid: content, path, file.", response.Stats.Hint);
    }

    [Fact]
    public async Task TotalHits_Is_Limit_Independent()
    {
        var tool = Tool();

        var response = await tool.SearchAsync("widget", limit: 1, match: "path");

        Assert.Single(response.Files!);
        Assert.Equal(2, response.Stats.TotalHits);
    }

    [Fact]
    public async Task File_Rows_Carry_ProjectRoot_ChunkCount_And_LastIndexedAt()
    {
        var tool = Tool();

        var response = await tool.SearchAsync("widget", match: "file");

        Assert.All(response.Files!, f =>
        {
            Assert.True(f.ChunkCount > 0);
            Assert.Equal(Path.GetFullPath(_workspace), f.ProjectRoot);
            Assert.NotNull(f.LastIndexedAt);
        });
    }

    [Fact]
    public async Task Content_Mode_Is_The_Default_And_Unchanged()
    {
        var tool = Tool();

        var response = await tool.SearchAsync("ZephyrWidget");

        Assert.Equal("content", response.Mode);
        Assert.Null(response.Files);
        Assert.NotEmpty(response.Hits);
    }

    [Fact]
    public async Task A_ProjectFilter_That_Excludes_Everything_Answers_In_The_Requested_Shape()
    {
        var tool = Tool();

        var filesRequest = await tool.SearchAsync("widget", match: "file", projectFilter: "a-workspace-name-that-cannot-match-zzzz");

        Assert.Equal("files", filesRequest.Mode);
        Assert.Empty(filesRequest.Files!);
        Assert.Equal(0, filesRequest.Stats.TotalHits);
        Assert.Equal("No indexed workspace matches projectFilter 'a-workspace-name-that-cannot-match-zzzz'.", filesRequest.Stats.Hint);

        var contentRequest = await tool.SearchAsync("widget", projectFilter: "a-workspace-name-that-cannot-match-zzzz");

        Assert.Equal("content", contentRequest.Mode);
        Assert.Null(contentRequest.Files);
        Assert.Equal(0, contentRequest.Stats.TotalHits);
        Assert.NotNull(contentRequest.Stats.Hint);
    }

    [Fact]
    public async Task A_Zero_Match_Files_Search_Does_Not_Fall_Back_To_Content()
    {
        var tool = Tool();

        var response = await tool.SearchAsync("no-such-file-name-anywhere", match: "file");

        Assert.Equal("files", response.Mode);
        Assert.Empty(response.Files!);
        Assert.Empty(response.Hits);
        Assert.NotNull(response.Stats.Hint);
    }

    [Fact]
    public async Task A_Sibling_Project_Is_Searched_Through_Explicit_Projects()
    {
        var sibling = Path.Combine(Path.GetTempPath(), "eling-match-sib-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sibling);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sibling, "SiblingNeedle.cs"), "class SiblingNeedle { }\n");
            var siblingSvc = new CodebaseIndexService(
                sibling,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(sibling)));
            await siblingSvc.IndexAsync(full: true);

            var tool = Tool();
            var response = await tool.SearchAsync("SiblingNeedle", match: "file", projects: [sibling]);

            Assert.Contains(response.Files!, f => f.ProjectRoot == Path.GetFullPath(sibling) && f.Path == "SiblingNeedle.cs");
        }
        finally
        {
            CodebaseRebuildScopeTests.TryDelete(sibling);
        }
    }

}
