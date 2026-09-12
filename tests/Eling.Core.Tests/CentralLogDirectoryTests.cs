using Eling.Core.Scope;

namespace Eling.Core.Tests;

public sealed class CentralLogDirectoryTests
{
    [Fact]
    public void Default_Linux_macos_path_returns_local_share()
    {
        // Arrange
        var expectedHome = Path.Combine(Path.GetTempPath(), "fake-home");
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
        var expectedHome = Path.Combine(Path.GetTempPath(), "fake-win-home");
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
        var userHome = Path.Combine(Path.GetTempPath(), "fake-home");
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
