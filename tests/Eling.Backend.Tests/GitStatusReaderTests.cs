using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Tests;

public sealed class GitStatusReaderTests
{
    [Fact]
    public void PorcelainParser_CleanOutput_HasNoEntries()
    {
        var state = GitStatusReader.ParsePorcelain("");

        Assert.False(state.HasStagedChanges);
        Assert.Empty(state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_StagedModification_IsDetected()
    {
        var state = GitStatusReader.ParsePorcelain("M  src/a.cs\n");

        Assert.True(state.HasStagedChanges);
        Assert.Equal(new[] { "src/a.cs" }, state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_UnstagedAndUntracked_AreNotStaged()
    {
        var state = GitStatusReader.ParsePorcelain(" M src/a.cs\n?? src/new.cs\n");

        Assert.False(state.HasStagedChanges);
        Assert.Empty(state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_RenamedEntry_UsesNewPath()
    {
        var state = GitStatusReader.ParsePorcelain("R  old/name.cs -> new/name.cs\n");

        Assert.True(state.HasStagedChanges);
        Assert.Equal(new[] { "new/name.cs" }, state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_QuotedPath_IsUnquoted()
    {
        var state = GitStatusReader.ParsePorcelain("A  \"src/quo ted.cs\"\n");

        Assert.True(state.HasStagedChanges);
        Assert.Equal(new[] { "src/quo ted.cs" }, state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_CrLfAndBlankLines_AreTolerated()
    {
        var state = GitStatusReader.ParsePorcelain("M  src/a.cs\r\n\r\nA  src/b.cs\r\n");

        Assert.True(state.HasStagedChanges);
        Assert.Equal(new[] { "src/a.cs", "src/b.cs" }, state.StagedPaths);
    }
}
