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
