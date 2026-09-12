using Eling.Core.Scope;

namespace Eling.Core.Tests;

/// <summary>
/// Scope chain rules: every ancestor `.eling` from the start directory upward
/// becomes a chain level, nearest first; user-home `.eling` is never a level.
/// </summary>
public sealed class ProjectScopeDiscoverChainTests : IDisposable
{
    private readonly string _root;

    public ProjectScopeDiscoverChainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-dummy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateDir(params string[] segments)
    {
        var path = Path.Combine([_root, ..segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Builds root/.eling, root/integrations/.eling, root/integrations/payments (no .eling).</summary>
    private (string Root, string Integrations, string Payments) CreateFixture()
    {
        var root = CreateDir("root");
        Directory.CreateDirectory(Path.Combine(root, ".eling"));
        var integrations = CreateDir("root", "integrations");
        Directory.CreateDirectory(Path.Combine(integrations, ".eling"));
        var payments = CreateDir("root", "integrations", "payments");
        return (root, integrations, payments);
    }

    [Fact]
    public void DiscoverChain_CollectsAllLevels_NearestFirst()
    {
        var (root, integrations, payments) = CreateFixture();

        var chain = ProjectScope.DiscoverChain(payments, stopAtDirectory: root);

        Assert.Equal(2, chain.Count);
        Assert.Equal(Path.GetFullPath(integrations), chain[0].Root);
        Assert.Equal(Path.GetFullPath(root), chain[1].Root);
    }

    [Fact]
    public void DiscoverChain_StartHasOwnEling_OwnScopeIsHead()
    {
        var (root, integrations, _) = CreateFixture();

        var chain = ProjectScope.DiscoverChain(integrations, stopAtDirectory: root);

        Assert.Equal(2, chain.Count);
        Assert.Equal(Path.GetFullPath(integrations), chain[0].Root);
        Assert.Equal(Path.GetFullPath(root), chain[1].Root);
    }

    [Fact]
    public void DiscoverChain_NoElingAnywhere_ReturnsEmpty()
    {
        var fresh = CreateDir("fresh", "nested");

        var chain = ProjectScope.DiscoverChain(fresh, stopAtDirectory: fresh);

        Assert.Empty(chain);
    }

    [Fact]
    public void DiscoverChain_UserHomeExcluded()
    {
        // Fake user home with .eling directly inside it: walking up must NOT add
        // the home directory as a level, even though stopAtDirectory includes it.
        var fakeHome = CreateDir("fake-home");
        Directory.CreateDirectory(Path.Combine(fakeHome, ".eling"));
        var nested = CreateDir("fake-home", "work", "payments");

        var chain = ProjectScope.DiscoverChain(nested, stopAtDirectory: fakeHome, userHomeDirectory: fakeHome);

        Assert.Empty(chain);
    }

    [Fact]
    public void DiscoverChain_StopAtSeam_DoesNotWalkAbove()
    {
        // .eling exists ABOVE the stopAt directory: the walk must not collect it.
        var outer = CreateDir("outer");
        Directory.CreateDirectory(Path.Combine(outer, ".eling"));
        var stop = CreateDir("outer", "stop");
        var nested = CreateDir("outer", "stop", "payments");

        var chain = ProjectScope.DiscoverChain(nested, stopAtDirectory: stop);

        Assert.Empty(chain);
    }
}
