using Eling.Backend.Mcp.Tools;
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
public sealed class CodebaseSearchMatchTests : IDisposable
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    private readonly string _workspace;
    private readonly string _dataDir;
    private readonly string? _originalDataDir;

    public CodebaseSearchMatchTests()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-match-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-match-data-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        WriteAsync("src/backend/Widget.cs", "public sealed class ZephyrWidget { }\n").GetAwaiter().GetResult();
        WriteAsync("src/widget/WidgetHelper.cs", "public sealed class WidgetHelper { }\n").GetAwaiter().GetResult();

        var svc = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
        svc.IndexAsync(full: true).GetAwaiter().GetResult();
    }

    private async Task<CodebaseSearchTool> ToolAsync() =>
        new(new CodebaseIndexService(
                _workspace,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace))),
            registry: null, logger: null);

    [Fact]
    public async Task File_Mode_Finds_A_File_By_Name()
    {
        var tool = await ToolAsync();

        var response = await tool.SearchAsync("widget", match: "file");

        Assert.Equal("files", response.Mode);
        Assert.Empty(response.Hits);
        Assert.Contains(response.Files!, f => f.Path == "src/backend/Widget.cs");
    }

    [Fact]
    public async Task Path_Mode_Finds_Files_Under_A_Folder_Name()
    {
        var tool = await ToolAsync();

        var response = await tool.SearchAsync("backend", match: "path");

        Assert.Equal("files", response.Mode);
        var file = Assert.Single(response.Files!);
        Assert.Equal("src/backend/Widget.cs", file.Path);
    }

    [Fact]
    public async Task File_Mode_Ignores_A_Folder_Name_In_The_Path()
    {
        var tool = await ToolAsync();

        var response = await tool.SearchAsync("backend", match: "file");

        Assert.Equal("files", response.Mode);
        Assert.Empty(response.Files!);
        Assert.NotNull(response.Stats.Hint);
    }

    [Fact]
    public async Task Match_Is_Case_And_Whitespace_Insensitive()
    {
        var tool = await ToolAsync();

        var response = await tool.SearchAsync("widget", match: " FILE ");

        Assert.Equal("files", response.Mode);
    }

    [Fact]
    public async Task An_Unknown_Match_Returns_A_Hint_With_Zero_Hits()
    {
        var tool = await ToolAsync();

        var response = await tool.SearchAsync("widget", match: "name");

        Assert.Equal("content", response.Mode);
        Assert.Equal(0, response.Stats.TotalHits);
        Assert.Equal("Invalid match 'name'. Valid: content, path, file.", response.Stats.Hint);
    }

    [Fact]
    public async Task TotalHits_Is_Limit_Independent()
    {
        var tool = await ToolAsync();

        var response = await tool.SearchAsync("widget", limit: 1, match: "path");

        Assert.Single(response.Files!);
        Assert.Equal(2, response.Stats.TotalHits);
    }

    [Fact]
    public async Task File_Rows_Carry_ProjectRoot_ChunkCount_And_LastIndexedAt()
    {
        var tool = await ToolAsync();

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
        var tool = await ToolAsync();

        var response = await tool.SearchAsync("ZephyrWidget");

        Assert.Equal("content", response.Mode);
        Assert.Null(response.Files);
        Assert.NotEmpty(response.Hits);
    }

    [Fact]
    public async Task A_ProjectFilter_That_Excludes_Everything_Reports_It()
    {
        var tool = await ToolAsync();

        var response = await tool.SearchAsync("widget", match: "file", projectFilter: "a-workspace-name-that-cannot-match-zzzz");

        Assert.Equal("content", response.Mode);
        Assert.Equal(0, response.Stats.TotalHits);
        Assert.Equal("No indexed workspace matches projectFilter 'a-workspace-name-that-cannot-match-zzzz'.", response.Stats.Hint);
    }

    [Fact]
    public async Task A_Zero_Match_Files_Search_Does_Not_Fall_Back_To_Content()
    {
        var tool = await ToolAsync();

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

            var tool = await ToolAsync();
            var response = await tool.SearchAsync("SiblingNeedle", match: "file", projects: [sibling]);

            Assert.Contains(response.Files!, f => f.ProjectRoot == Path.GetFullPath(sibling) && f.Path == "SiblingNeedle.cs");
        }
        finally
        {
            CodebaseRebuildScopeTests.TryDelete(sibling);
        }
    }

    private async Task WriteAsync(string relativePath, string contents)
    {
        var full = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, contents);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        CodebaseRebuildScopeTests.TryDelete(_workspace);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
    }
}
