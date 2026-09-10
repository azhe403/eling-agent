namespace Eling.Core.Tests;

/// <summary>
/// Pecut 9 scope rules: `.eling` is the ONLY project-scope authority; the user
/// scope is independent of the project scope.
/// </summary>
public sealed class ProjectScopeTests : IDisposable
{
    private readonly string _root;

    public ProjectScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-scope-tests-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Discover_AncestorHasElingDirectory_FindsIt()
    {
        var project = CreateDir("project");
        Directory.CreateDirectory(Path.Combine(project, ".eling"));
        var nested = CreateDir("project", "src", "backend", "Eling.Host");

        var scope = ProjectScope.Discover(nested, stopAtDirectory: _root);

        Assert.Equal(Path.GetFullPath(project), scope.Root);
        Assert.Equal(Path.Combine(project, ".eling"), scope.DataDirectory);
    }

    [Fact]
    public void Discover_MultipleElingDirectories_NearestWins()
    {
        var outer = CreateDir("outer");
        Directory.CreateDirectory(Path.Combine(outer, ".eling"));
        var inner = CreateDir("outer", "inner");
        Directory.CreateDirectory(Path.Combine(inner, ".eling"));
        var leaf = CreateDir("outer", "inner", "deep");

        var scope = ProjectScope.Discover(leaf, stopAtDirectory: _root);

        Assert.Equal(Path.GetFullPath(inner), scope.Root);
    }

    [Theory]
    [InlineData("solution.slnx")]
    [InlineData("solution.sln")]
    public void Discover_SolutionFileWithoutEling_IsNotScopeAuthority(string solutionFile)
    {
        // A solution file without any .eling anywhere must NOT become a scope root.
        var dir = CreateDir("sln-dir");
        File.WriteAllText(Path.Combine(dir, solutionFile), "");
        var nested = CreateDir("sln-dir", "src");

        var scope = ProjectScope.Discover(nested, stopAtDirectory: _root);

        // Falls back to the start directory itself; the .slnx/.sln is ignored.
        Assert.Equal(Path.GetFullPath(nested), scope.Root);
    }

    [Fact]
    public void Discover_NoElingDirectory_FallsBackToStartDirectory()
    {
        var dir = CreateDir("fresh");

        var scope = ProjectScope.Discover(dir, stopAtDirectory: _root);

        Assert.Equal(Path.GetFullPath(dir), scope.Root);
        Assert.Equal(Path.Combine(dir, ".eling"), scope.DataDirectory);
    }

    [Fact]
    public void Discover_NoOverride_DefaultsToCurrentWorkingDirectory()
    {
        var original = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_root);

            var scope = ProjectScope.Discover(stopAtDirectory: _root);

            Assert.Equal(Path.GetFullPath(_root), scope.Root);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }
}

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

public sealed class UserScopeTests
{
    [Fact]
    public void Resolve_NoOverride_DefaultsToPerUserConfigDirectory()
    {
        var scope = UserScope.Resolve();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "eling");
        Assert.Equal(expected, scope.Root);
        Assert.Equal(Path.Combine(expected, "config"), scope.ConfigDirectory);
        Assert.Equal(Path.Combine(expected, "runtime"), scope.RuntimeDirectory);
    }

    [Fact]
    public void Resolve_OverridePathProvided_WinsOverDefault()
    {
        var overrideRoot = Path.Combine(Path.GetTempPath(), "eling-user-override-" + Guid.NewGuid().ToString("N")[..8]);

        var scope = UserScope.Resolve(overrideRoot);

        Assert.Equal(Path.GetFullPath(overrideRoot), scope.Root);
    }

    [Fact]
    public void Resolve_UserScopeConfigured_IndependentOfProjectScope()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "eling-independence-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(projectRoot);

        var project = ProjectScope.Discover(projectRoot);
        var user = UserScope.Resolve();

        // The user scope never falls back to a project location and vice versa.
        Assert.NotEqual(project.Root, user.Root);
        Assert.DoesNotContain(".eling", user.Root);
        Assert.NotEqual(user.Root, project.DataDirectory);

        try { Directory.Delete(projectRoot, recursive: true); } catch { }
    }
}

public sealed class CentralLogDirectoryTests
{
    [Fact]
    public void Default_Linux_macos_path_returns_local_share()
    {
        // Arrange
        var expectedHome = "/home/user";
        var expected = Path.Combine(expectedHome, ".local", "share", "eling", "logs");

        // Act
        var actual = CentralLogDirectory.Resolve(userHome: expectedHome);

        // Assert
        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public void Default_Windows_path_returns_local_share()
    {
        // Arrange
        var expectedHome = @"C:\Users\some-user";
        var expected = Path.Combine(expectedHome, ".local", "share", "eling", "logs");

        // Act
        var actual = CentralLogDirectory.Resolve(userHome: expectedHome);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void XDG_override_is_honoured_on_all_platforms()
    {
        // Arrange
        var xdgDataHome = "/custom/data";
        var userHome = "/home/user";
        var expected = Path.Combine(xdgDataHome, "eling", "logs");

        // Act
        var actual = CentralLogDirectory.Resolve(xdgDataHome: xdgDataHome, userHome: userHome);

        // Assert
        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public void Directory_is_created_on_resolve()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}");
        var expectedHome = tempDir;
        var expected = Path.Combine(tempDir, ".local", "share", "eling", "logs");

        // Act
        var actual = CentralLogDirectory.Resolve(userHome: expectedHome);

        // Assert
        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public void Null_or_whitespace_userHome_falls_back_to_SpecialFolder_UserProfile()
    {
        // Arrange
        var expectedHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(expectedHome, ".local", "share", "eling", "logs");

        // Act & Assert for null
        var actualNull = CentralLogDirectory.Resolve(userHome: null);
        Assert.Equal(expected, actualNull);

        // Act & Assert for whitespace
        var actualWhitespace = CentralLogDirectory.Resolve(userHome: "   ");
        Assert.Equal(expected, actualWhitespace);
    }

    [Fact]
    public void Empty_xdgDataHome_is_ignored()
    {
        // Arrange
        var expectedHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(expectedHome, ".local", "share", "eling", "logs");

        // Act
        var actual = CentralLogDirectory.Resolve(xdgDataHome: "", userHome: expectedHome);

        // Assert
        Assert.Equal(expected, actual);
    }
}

public sealed class ProjectIdTests
{
    [Fact]
    public void Returns_UserScope_when_isUserHome_is_true()
    {
        // Arrange
        ProjectScope? scope = null; // doesn't matter when isUserHome true
        bool isUserHome = true;

        // Act
        var actual = ProjectId.FromScope(scope, isUserHome);

        // Assert
        Assert.Equal("UserScope", actual);
    }

    [Fact]
    public void Returns_DirectoryName_for_normal_project_scope()
    {
        // Arrange
        var tempRoot = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRoot);
        var scope = new ProjectScope(tempRoot);
        bool isUserHome = false; // not running at user home

        // Act
        var actual = ProjectId.FromScope(scope, isUserHome);

        // Assert
        Assert.Equal(Path.GetFileName(tempRoot), actual);
    }

    [Fact]
    public void Returns_unknown_when_scope_is_null()
    {
        // Arrange
        ProjectScope? scope = null;
        bool isUserHome = false;

        // Act
        var actual = ProjectId.FromScope(scope, isUserHome);

        // Assert
        Assert.Equal("unknown", actual);
    }
}