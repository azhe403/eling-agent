using Eling.Backend.Mcp.Tools;
using Eling.Core.Codebase;
using Eling.Core.Scope;

namespace Eling.Backend.Tests;

/// <summary>
/// The content-mode path filter: the query stays a normal content FTS, but
/// only files whose workspace-relative path contains the given text answer
/// back. The substring may sit anywhere in the path - unlike pathPrefix,
/// which is anchored at the start - so 'utils' finds src/utils/... . The
/// class joins the ELING_DATA_DIR collection because the federated test
/// reads sibling indexes from the global store under that variable.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseSearchMatchPathTests : IDisposable
{
    private const string DataDirEnv = "ELING_DATA_DIR";
    private const string Needle = "zephyrpathneedle";

    private readonly string _workspace;
    private readonly string _dataDir;
    private readonly string? _originalDataDir;

    public CodebaseSearchMatchPathTests()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-matchpath-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-matchpath-data-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        WriteAsync("src/utils/ZephyrUtil.cs", "public sealed class ZephyrPathneedle { }\n").GetAwaiter().GetResult();
        WriteAsync("src/backend/ZephyrBack.cs", "public sealed class ZephyrPathneedle { }\n").GetAwaiter().GetResult();
        WriteAsync("src/lit_a/ZephyrUnder.cs", "public sealed class ZephyrPathneedle { }\n").GetAwaiter().GetResult();
        WriteAsync("src/lita/ZephyrPlain.cs", "public sealed class ZephyrPathneedle { }\n").GetAwaiter().GetResult();

        IndexedAsync(_workspace).GetAwaiter().GetResult();
    }

    private async Task IndexedAsync(string root)
    {
        var svc = new CodebaseIndexService(
            root,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(root)));
        await svc.IndexAsync(full: true);
    }

    private CodebaseSearchTool Tool() =>
        new(new CodebaseIndexService(
                _workspace,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace))),
            registry: null, logger: null);

    [Fact]
    public async Task Content_Search_Can_Be_Narrowed_To_A_Path_Substring()
    {
        var unfiltered = await Tool().SearchAsync(Needle);
        var utils = await Tool().SearchAsync(Needle, matchPath: "utils");

        Assert.True(unfiltered.Hits.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2);
        Assert.All(utils.Hits, h => Assert.Contains("src/utils/", h.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MatchPath_Is_Not_Anchored_At_The_Start_Of_The_Path()
    {
        // 'utils' sits in the middle of src/utils/ZephyrUtil.cs - a prefix-like
        // match would find nothing, a substring match finds exactly that file.
        var response = await Tool().SearchAsync(Needle, matchPath: "utils");

        Assert.Contains(response.Hits, h => h.Path.Contains("src/utils/ZephyrUtil.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response.Hits, h => h.Path.Contains("ZephyrBack.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_MatchPath_That_Matches_Nothing_Returns_No_Hits()
    {
        var response = await Tool().SearchAsync(Needle, matchPath: "no-such-folder-name-anywhere");

        Assert.Equal("content", response.Mode);
        Assert.Empty(response.Hits);
        Assert.Equal(0, response.Stats.TotalHits);
        Assert.Equal("No hits matched the query and filters.", response.Stats.Hint);
    }

    [Fact]
    public async Task MatchPath_Ignores_Case()
    {
        var response = await Tool().SearchAsync(Needle, matchPath: "UTILS");

        Assert.NotEmpty(response.Hits);
        Assert.All(response.Hits, h => Assert.Contains("src/utils/", h.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_Blank_MatchPath_Is_Treated_As_Absent()
    {
        var blank = await Tool().SearchAsync(Needle, matchPath: "   ");
        var none = await Tool().SearchAsync(Needle);

        Assert.Equal(none.Hits.Count, blank.Hits.Count);
    }

    [Fact]
    public async Task An_Underscore_MatchPath_Does_Not_Act_As_A_Wildcard()
    {
        var response = await Tool().SearchAsync(Needle, matchPath: "_");

        Assert.NotEmpty(response.Hits);
        Assert.All(response.Hits, h => Assert.Contains("_", h.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchPath_Is_Ignored_In_File_Mode()
    {
        var response = await Tool().SearchAsync("Zephyr", match: "file", matchPath: "definitely-not-present-anywhere");

        Assert.Equal("files", response.Mode);
        Assert.Contains(response.Files!, f => f.Path == "src/utils/ZephyrUtil.cs");
        Assert.Contains(response.Files!, f => f.Path == "src/backend/ZephyrBack.cs");
    }

    [Fact]
    public async Task MatchPath_Applies_To_Sibling_Roots_In_An_Explicit_Projects_Call()
    {
        var sibling = Path.Combine(Path.GetTempPath(), "eling-matchpath-sib-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(sibling, "libs/utils"));
        Directory.CreateDirectory(Path.Combine(sibling, "libs/backend"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sibling, "libs/utils/ZephyrSiblingUtil.cs"), "public sealed class ZephyrPathneedle { }\n");
            await File.WriteAllTextAsync(Path.Combine(sibling, "libs/backend/ZephyrSiblingBackend.cs"), "public sealed class ZephyrPathneedle { }\n");
            await IndexedAsync(sibling);

            var response = await Tool().SearchAsync(Needle, matchPath: "utils", projects: [sibling]);

            Assert.All(response.Hits, h =>
            {
                Assert.Equal(Path.GetFullPath(sibling), h.ProjectRoot);
                Assert.Contains("utils", h.Path, StringComparison.OrdinalIgnoreCase);
            });
            Assert.DoesNotContain(response.Hits, h => h.Path.Contains("backend", StringComparison.OrdinalIgnoreCase));
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
