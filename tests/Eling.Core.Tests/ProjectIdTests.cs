using Eling.Core.Projects;

namespace Eling.Core.Tests;

/// <summary>
/// The project identity is the opened workspace, not the <c>.eling</c> scope
/// that owns the memories. Those differ whenever the opened folder sits inside
/// a larger scope, and the log must name what was opened.
/// </summary>
public sealed class ProjectIdTests
{
    [Fact]
    public void FromWorkspace_Returns_UserScope_when_isUserHome_is_true()
    {
        // Arrange
        var workspace = Path.Combine(Path.GetTempPath(), "workspace", "execution-order");
        bool isUserHome = true;

        // Act
        var actual = ProjectId.FromWorkspace(workspace, isUserHome);

        // Assert
        Assert.Equal("UserScope", actual);
    }

    [Fact]
    public void FromWorkspace_Returns_DirectoryName_of_opened_folder()
    {
        // Arrange
        var workspace = Path.Combine(Path.GetTempPath(), "workspace", "execution-order");
        bool isUserHome = false;

        // Act
        var actual = ProjectId.FromWorkspace(workspace, isUserHome);

        // Assert
        Assert.Equal("execution-order", actual);
    }

    [Fact]
    public void FromWorkspace_Returns_unknown_when_workspace_is_blank()
    {
        // Arrange
        string? workspace = null;
        bool isUserHome = false;

        // Act
        var actual = ProjectId.FromWorkspace(workspace, isUserHome);

        // Assert
        Assert.Equal("unknown", actual);
    }

    [Fact]
    public void FromWorkspace_Ignores_trailing_separator()
    {
        // Arrange
        var workspace = Path.Combine(Path.GetTempPath(), "workspace", "execution-order") + Path.DirectorySeparatorChar;
        bool isUserHome = false;

        // Act
        var actual = ProjectId.FromWorkspace(workspace, isUserHome);

        // Assert
        Assert.Equal("execution-order", actual);
    }

    [Fact]
    public void FromWorkspace_Names_the_opened_folder_not_the_enclosing_scope()
    {
        // The failure this guards: a workspace opened inside a larger scope was
        // logged under the SCOPE's name, so every service under one scope
        // produced the same project id and the log could not tell them apart.
        // Arrange
        var scopeRoot = Path.Combine(Path.GetTempPath(), "workspace", "iron-bird");
        var opened = Path.Combine(scopeRoot, "execution-order");
        bool isUserHome = false;

        // Act
        var actual = ProjectId.FromWorkspace(opened, isUserHome);

        // Assert
        Assert.Equal("execution-order", actual);
        Assert.NotEqual("iron-bird", actual);
    }
}
