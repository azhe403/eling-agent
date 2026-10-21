namespace Eling.Backend.Tests;

/// <summary>
/// The appsettings source drifted once: the dashboard host scanned the whole
/// workspace directory while the MCP host read a single appsettings.json, so an
/// appsettings.Staging.json surfaced over REST and never over the MCP tools.
/// These tests pin the resolution to one place so the two hosts cannot drift
/// apart again.
/// </summary>
public sealed class DatabaseSourceConventionTests
{
    private static readonly string[] HostFiles =
    [
        "src/backend/Eling.Backend/Bootstrap/McpHostBuilder.cs",
        "src/backend/Eling.Backend/Bootstrap/DashboardServices.cs",
    ];

    [Fact]
    public void EveryHost_ResolvesTheAppSettingsSource_ThroughTheSharedResolver()
    {
        var repoRoot = RepoRoot();
        foreach (var host in HostFiles)
        {
            var path = Path.Combine(repoRoot, host.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(path);

            Assert.Contains("AppSettingsSourceResolver.Resolve", text);
            Assert.DoesNotContain("ELING_DATABASE_DETECT_SOURCE", text);
        }
    }

    [Fact]
    public void TheDetectSourceOverride_IsReadInExactlyOnePlace()
    {
        var readers = Sources()
            .Where(p => File.ReadAllText(p).Contains("ELING_DATABASE_DETECT_SOURCE"))
            .Select(p => Path.GetRelativePath(RepoRoot(), p).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        Assert.Equal(
            new[] { "src/backend/Eling.Core/Database/AppSettingsSourceResolver.cs" },
            readers.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TheResolver_LivesBesideTheScanner()
    {
        var path = Path.Combine(
            RepoRoot(),
            "src", "backend", "Eling.Core", "Database", "AppSettingsSourceResolver.cs");

        Assert.True(File.Exists(path));
    }

    /// <summary>Every hand-written source file under src, build output excluded.</summary>
    private static IReadOnlyList<string> Sources()
    {
        var root = Path.Combine(RepoRoot(), "src");
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Eling.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repo root (Eling.slnx) not found from test output.");
    }
}
