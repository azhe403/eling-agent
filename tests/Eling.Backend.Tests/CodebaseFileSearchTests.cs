using Eling.Core.Codebase;

namespace Eling.Backend.Tests;

/// <summary>
/// The file half of the codebase search: the query is matched against the
/// indexed file paths instead of the indexed contents. The two scopes differ
/// in exactly one thing — "path" sees the folders, "file" sees only the name
/// — and that difference is the whole point of the feature, so both are
/// pinned here rather than only the happy path.
/// </summary>
public sealed class CodebaseFileSearchTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _dataDir;

    public CodebaseFileSearchTests()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-filesearch-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-filesearch-db-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);
    }

    [Fact]
    public async Task PathScope_FindsAFileByAFolderNameInItsPath()
    {
        await WriteAsync("src/backend/Widget.cs");
        var svc = await IndexedAsync();

        var files = await svc.SearchFilesAsync("backend", limit: 10, match: CodebaseFileMatch.Path);

        Assert.Single(files);
        Assert.Equal("src/backend/Widget.cs", files[0].Path);
    }

    [Fact]
    public async Task FileScope_IgnoresAFolderNameInThePath()
    {
        await WriteAsync("src/backend/Widget.cs");
        var svc = await IndexedAsync();

        var files = await svc.SearchFilesAsync("backend", limit: 10, match: CodebaseFileMatch.FileName);

        Assert.Empty(files);
    }

    [Fact]
    public async Task FileScope_FindsAFileByItsName()
    {
        await WriteAsync("src/backend/Widget.cs");
        var svc = await IndexedAsync();

        var files = await svc.SearchFilesAsync("widget", limit: 10, match: CodebaseFileMatch.FileName);

        Assert.Single(files);
        Assert.Equal("src/backend/Widget.cs", files[0].Path);
    }

    [Fact]
    public async Task TheMatchIgnoresCase()
    {
        await WriteAsync("src/backend/Widget.cs");
        var svc = await IndexedAsync();

        var files = await svc.SearchFilesAsync("WIDGET", limit: 10, match: CodebaseFileMatch.FileName);

        Assert.Single(files);
    }

    [Fact]
    public async Task ANeedleThatMatchesNothingReturnsNoFiles()
    {
        await WriteAsync("src/backend/Widget.cs");
        var svc = await IndexedAsync();

        var files = await svc.SearchFilesAsync("absent", limit: 10, match: CodebaseFileMatch.Path);

        Assert.Empty(files);
    }

    [Fact]
    public async Task AFileIsFoundByItsNameOnlyOnce_NoMatterHowOftenTheNameAppears()
    {
        // The path scope sees the name twice — once as the file name and once
        // in the folder — but a file is a file: the answer is one row, not
        // one row per occurrence.
        await WriteAsync("src/widget/Widget.cs");
        var svc = await IndexedAsync();

        var files = await svc.SearchFilesAsync("widget", limit: 10, match: CodebaseFileMatch.Path);

        Assert.Single(files);
    }

    [Fact]
    public async Task ResultsCarryTheOwningWorkspaceAndTheChunkCount()
    {
        await WriteAsync("src/backend/Widget.cs");
        var svc = await IndexedAsync();

        var files = await svc.SearchFilesAsync("Widget", limit: 10, match: CodebaseFileMatch.FileName);

        var file = Assert.Single(files);
        Assert.Equal(Path.GetFullPath(_workspace), file.ProjectRoot);
        Assert.True(file.ChunkCount > 0);
    }

    [Fact]
    public async Task ABlankNeedleReturnsNoFiles()
    {
        await WriteAsync("src/backend/Widget.cs");
        var svc = await IndexedAsync();

        var files = await svc.SearchFilesAsync("   ", limit: 10, match: CodebaseFileMatch.Path);

        Assert.Empty(files);
    }

    private async Task WriteAsync(string relativePath)
    {
        var full = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, "public sealed class ZephyrWidget { }\n");
    }

    private async Task<CodebaseIndexService> IndexedAsync()
    {
        // The DB lives outside the workspace so the indexer never walks its own
        // sidecar files.
        var svc = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(Path.Combine(_dataDir, "codebase.db")));
        await svc.IndexAsync(full: true);
        return svc;
    }

    public void Dispose()
    {
        CodebaseRebuildScopeTests.TryDelete(_workspace);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
    }
}
