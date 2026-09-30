using Eling.Core.Scope;

namespace Eling.Core.Tests;

public sealed class UserScopeTests
{
    [Fact]
    public void Resolve_NoOverride_DefaultsToPerUserConfigDirectory()
    {
        var scope = UserScope.Resolve();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expectedConfig = Path.Combine(home, ".config", "eling");
        var expectedData = Path.Combine(home, ".local", "share", "eling");

        Assert.Equal(expectedConfig, scope.Root);
        Assert.Equal(Path.Combine(expectedConfig, "config"), scope.ConfigDirectory);
        Assert.Equal(Path.Combine(expectedData, "runtime"), scope.RuntimeDirectory);
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
