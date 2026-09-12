using Eling.Core.Scope;

namespace Eling.Core.Tests;

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
