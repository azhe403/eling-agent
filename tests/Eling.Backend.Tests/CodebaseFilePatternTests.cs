using Eling.Core.Codebase;

namespace Eling.Backend.Tests;

/// <summary>
/// The file filter has to run inside the FTS statement, ahead of LIMIT.
/// Filtering the already-truncated result set looks like it works and quietly
/// returns short answers: a limit of 2 over a mixed index takes 2 arbitrary
/// ranked rows, and a filter applied afterwards then discards whichever of
/// them happened not to match.
/// </summary>
public sealed class CodebaseFilePatternTests : IDisposable
{
    private const string Marker = "zarquon";

    private readonly string _workspace;
    private readonly string _dataDir;

    public CodebaseFilePatternTests()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-filepattern-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-filepattern-db-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);
    }

    [Fact]
    public async Task FilePatternFiltersBeforeTheLimitIsApplied()
    {
        // The .md files are a single line repeating the term, so BM25 ranks them
        // above the long .cs files that mention it once. Unfiltered, the top two
        // ranked rows are both .md — exactly the case where filtering after the
        // limit would hand back nothing at all.
        for (var i = 0; i < 4; i++)
        {
            File.WriteAllText(
                Path.Combine(_workspace, $"b{i}.md"),
                string.Join(' ', Enumerable.Repeat(Marker, 5)) + "\n");
            File.WriteAllText(
                Path.Combine(_workspace, $"a{i}.cs"),
                "// " + Marker + "\n"
                + string.Join('\n', Enumerable.Range(0, 30).Select(n => $"// filler line {n}")));
        }
        var svc = await IndexedAsync();

        var hits = await svc.SearchAsync(Marker, limit: 2, filePattern: "*.cs");

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.EndsWith(".cs", h.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BareFragmentMatchesAnywhereInThePath()
    {
        File.WriteAllText(Path.Combine(_workspace, "notes.md"), Marker + "\n");
        File.WriteAllText(Path.Combine(_workspace, "other.cs"), Marker + "\n");
        var svc = await IndexedAsync();

        var hits = await svc.SearchAsync(Marker, limit: 10, filePattern: "notes");

        Assert.Single(hits);
        Assert.EndsWith("notes.md", hits[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LikeWildcardsInTheFilter_AreMatchedLiterally()
    {
        // "_" is a LIKE wildcard. Unescaped, "*_name.cs" would also match
        // weirdXname.cs and quietly return more than the caller asked for.
        File.WriteAllText(Path.Combine(_workspace, "weird_name.cs"), Marker + "\n");
        File.WriteAllText(Path.Combine(_workspace, "weirdXname.cs"), Marker + "\n");
        var svc = await IndexedAsync();

        var hits = await svc.SearchAsync(Marker, limit: 10, filePattern: "*_name.cs");

        Assert.Single(hits);
        Assert.EndsWith("weird_name.cs", hits[0].Path, StringComparison.Ordinal);
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
