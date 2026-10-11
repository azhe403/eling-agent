using Eling.Core.Scope;

namespace Eling.Core.Tests;

public sealed class CentralAgentDirectoryTests
{
    [Fact]
    public void Default_ReturnsLocalShare_AndCreatesDir()
    {
        // Arrange
        var home = Path.Combine(Path.GetTempPath(), $"fake-agent-home-{Guid.NewGuid():N}");
        var expected = Path.Combine(home, ".local", "share", "eling");

        // Act
        var actual = CentralAgentDirectory.Resolve(userHome: home);

        // Assert
        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public void WithSubDirectory_AppendsSegment_AndCreatesDir()
    {
        // Arrange
        var home = Path.Combine(Path.GetTempPath(), $"fake-agent-home-{Guid.NewGuid():N}");
        var expected = Path.Combine(home, ".local", "share", "eling", "workspaces");

        // Act
        var actual = CentralAgentDirectory.Resolve("workspaces", userHome: home);

        // Assert
        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public void XdgOverride_IsHonoured()
    {
        // Arrange
        var xdg = Path.Combine(Path.GetTempPath(), $"fake-xdg-{Guid.NewGuid():N}");
        var home = Path.Combine(Path.GetTempPath(), $"fake-home-{Guid.NewGuid():N}");
        var expected = Path.Combine(xdg, "eling");

        // Act
        var actual = CentralAgentDirectory.Resolve(xdgDataHome: xdg, userHome: home);

        // Assert
        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public void EnvAgentDataDir_OverridesAll()
    {
        // Arrange
        var custom = Path.Combine(Path.GetTempPath(), $"fake-env-base-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("ELING_AGENT_DATA_DIR");
        Environment.SetEnvironmentVariable("ELING_AGENT_DATA_DIR", custom);
        try
        {
            var expected = Path.GetFullPath(custom);

            // Act
            var actual = CentralAgentDirectory.Resolve(
                userHome: Path.Combine(Path.GetTempPath(), $"ignored-{Guid.NewGuid():N}"));

            // Assert
            Assert.Equal(expected, actual);
            Assert.True(Directory.Exists(actual));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_AGENT_DATA_DIR", previous);
        }
    }

    [Fact]
    public void EnvAgentDataDir_WithSubDirectory_Combines()
    {
        // Arrange
        var custom = Path.Combine(Path.GetTempPath(), $"fake-env-base-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("ELING_AGENT_DATA_DIR");
        Environment.SetEnvironmentVariable("ELING_AGENT_DATA_DIR", custom);
        try
        {
            var expected = Path.Combine(Path.GetFullPath(custom), "chats");

            // Act
            var actual = CentralAgentDirectory.Resolve("chats");

            // Assert
            Assert.Equal(expected, actual);
            Assert.True(Directory.Exists(actual));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_AGENT_DATA_DIR", previous);
        }
    }

    [Fact]
    public void NullUserHome_FallsBack_NoThrow()
    {
        // Arrange
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, ".local", "share", "eling");

        // Act
        var actual = CentralAgentDirectory.Resolve(userHome: null);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EmptyXdg_IsIgnored()
    {
        // Arrange
        var home = Path.Combine(Path.GetTempPath(), $"fake-agent-home-{Guid.NewGuid():N}");
        var expected = Path.Combine(home, ".local", "share", "eling");

        // Act
        var actual = CentralAgentDirectory.Resolve(xdgDataHome: "", userHome: home);

        // Assert
        Assert.Equal(expected, actual);
    }
}
