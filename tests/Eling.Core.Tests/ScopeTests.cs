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
    public void Ancestor_eling_directory_is_discovered()
    {
        var project = CreateDir("project");
        Directory.CreateDirectory(Path.Combine(project, ".eling"));
        var nested = CreateDir("project", "src", "backend", "Eling.Host");

        var scope = ProjectScope.Discover(nested, stopAtDirectory: _root);

        Assert.Equal(Path.GetFullPath(project), scope.Root);
        Assert.Equal(Path.Combine(project, ".eling"), scope.DataDirectory);
    }

    [Fact]
    public void Nearest_eling_wins_over_higher_ancestor()
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
    public void Solution_files_are_not_scope_authority(string solutionFile)
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
    public void Missing_eling_falls_back_to_start_directory()
    {
        var dir = CreateDir("fresh");

        var scope = ProjectScope.Discover(dir, stopAtDirectory: _root);

        Assert.Equal(Path.GetFullPath(dir), scope.Root);
        Assert.Equal(Path.Combine(dir, ".eling"), scope.DataDirectory);
    }

    [Fact]
    public void Discover_defaults_to_current_working_directory()
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

public sealed class UserScopeTests
{
    [Fact]
    public void Resolve_defaults_to_per_user_config_directory()
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
    public void Override_path_wins()
    {
        var overrideRoot = Path.Combine(Path.GetTempPath(), "eling-user-override-" + Guid.NewGuid().ToString("N")[..8]);

        var scope = UserScope.Resolve(overrideRoot);

        Assert.Equal(Path.GetFullPath(overrideRoot), scope.Root);
    }

    [Fact]
    public void User_scope_is_independent_of_project_scope()
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
