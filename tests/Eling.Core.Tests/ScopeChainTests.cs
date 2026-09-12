using Eling.Core.Scope;

namespace Eling.Core.Tests;

public sealed class ScopeChainTests : IDisposable
{
    private readonly string _root;

    public ScopeChainTests()
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

    [Fact]
    public void HasOwnScope_True_WhenCwdHasEling()
    {
        var root = CreateDir("root");
        Directory.CreateDirectory(Path.Combine(root, ".eling"));

        var chain = ScopeChain.Discover(root, stopAtDirectory: root);

        Assert.True(chain.HasOwnScope);
        Assert.NotNull(chain.Head);
        Assert.Equal(Path.GetFullPath(root), chain.Head!.Root);
        Assert.True(chain.IsInitialized);
    }

    [Fact]
    public void AncestorOnly_HeadIsNearestAncestor()
    {
        var root = CreateDir("root");
        Directory.CreateDirectory(Path.Combine(root, ".eling"));
        var integrations = CreateDir("root", "integrations");
        Directory.CreateDirectory(Path.Combine(integrations, ".eling"));
        var payments = CreateDir("root", "integrations", "payments");

        var chain = ScopeChain.Discover(payments, stopAtDirectory: root);

        Assert.False(chain.HasOwnScope);
        Assert.NotNull(chain.Head);
        Assert.Equal(Path.GetFullPath(integrations), chain.Head!.Root);
        Assert.True(chain.IsInitialized);
    }

    [Fact]
    public void NoEling_Uninitialized()
    {
        var fresh = CreateDir("fresh", "nested");

        var chain = ScopeChain.Discover(fresh, stopAtDirectory: fresh);

        Assert.False(chain.IsInitialized);
        Assert.Null(chain.Head);
        Assert.False(chain.HasOwnScope);
    }
}
