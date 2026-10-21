using Eling.Core.Scope;

namespace Eling.Core.Tests;

public sealed class ElingPathsTests
{
    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal("eling", ElingPaths.AppName);
        Assert.Equal(".eling", ElingPaths.DotElingDirName);
        Assert.Equal("memories", ElingPaths.MemoriesDirName);
        Assert.Equal("runtime", ElingPaths.RuntimeDirName);
        Assert.Equal("memory.db", ElingPaths.MemoryDbFileName);
        Assert.Equal("index.db", ElingPaths.LegacyMemoryDbFileName);
        Assert.Equal("codebase.db", ElingPaths.CodebaseDbFileName);
        Assert.Equal("projects", ElingPaths.ProjectsDirName);
    }

    [Fact]
    public void IsTemporaryPath_InsideTheTempArea_IsScratch()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "eling-scratch-" + Guid.NewGuid().ToString("N")[..8]);

        Assert.True(ElingPaths.IsTemporaryPath(scratch));
    }

    [Fact]
    public void IsTemporaryPath_UnderTheTempArea_AtAnyDepth_IsScratch()
    {
        var nested = Path.Combine(Path.GetTempPath(), "eling-scratch", "child", "grandchild");

        Assert.True(ElingPaths.IsTemporaryPath(nested));
    }

    [Fact]
    public void IsTemporaryPath_SiblingWithASimilarName_IsNotScratch()
    {
        // "TempX" beside "Temp" is a different folder. The comparison is
        // path-bounded on purpose: a bare name prefix would call it scratch.
        var sibling = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) + "X";

        Assert.False(ElingPaths.IsTemporaryPath(sibling));
    }

    [Fact]
    public void IsTemporaryPath_AProjectElsewhere_IsNotScratch()
    {
        var project = Path.Combine(
            Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:",
            "work",
            "project");

        Assert.False(ElingPaths.IsTemporaryPath(project));
    }

    [Fact]
    public void IsTemporaryPath_EmptyPath_IsNotScratch()
    {
        Assert.False(ElingPaths.IsTemporaryPath(string.Empty));
    }

    [Fact]
    public void ResolveCodebaseDbPath_UsesProjectFolderName_NotElingPrefix()
    {
        var result = ElingPaths.ResolveCodebaseDbPath("/work/hello-world", "/xdg/data", userHome: null, elingDataDir: null);

        var fileName = Path.GetFileName(result);
        Assert.StartsWith("hello-world-", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".db", fileName, StringComparison.Ordinal);
        Assert.DoesNotContain("eling", fileName, StringComparison.OrdinalIgnoreCase);
        var dir = Path.GetDirectoryName(result)!.Replace('\\', '/');
        Assert.Equal("/xdg/data/eling/codebase", dir);
    }

    [Fact]
    public void ResolveCodebaseDbPath_SameNameDifferentPath_UsesDifferentDb()
    {
        var a = ElingPaths.ResolveCodebaseDbPath("/work/hello-world", "/xdg/data");
        var b = ElingPaths.ResolveCodebaseDbPath("/other/hello-world", "/xdg/data");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ResolveProjectLocalDir_UsesProjectFolderName()
    {
        var result = ElingPaths.ResolveProjectLocalDir("/work/hello-world", "/xdg/data");

        var shard = Path.GetFileName(result);
        Assert.StartsWith("hello-world-", shard, StringComparison.Ordinal);
        var dir = Path.GetDirectoryName(result)!.Replace('\\', '/');
        Assert.Equal("/xdg/data/eling/projects", dir);
    }

    [Fact]
    public void ResolveProjectLocalDir_MatchesCodebaseShardScheme()
    {
        var local = ElingPaths.ResolveProjectLocalDir("/work/hello-world", "/xdg/data");
        var codebase = ElingPaths.ResolveCodebaseDbPath("/work/hello-world", "/xdg/data");

        Assert.Equal(Path.GetFileName(local), Path.GetFileNameWithoutExtension(codebase));
    }

    [Fact]
    public void ResolveProjectLocalDir_SameNameDifferentPath_UsesDifferentShard()
    {
        var a = ElingPaths.ResolveProjectLocalDir("/work/hello-world", "/xdg/data");
        var b = ElingPaths.ResolveProjectLocalDir("/other/hello-world", "/xdg/data");

        Assert.NotEqual(a, b);
    }
}
