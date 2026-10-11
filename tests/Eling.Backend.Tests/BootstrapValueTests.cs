using Eling.Backend.Bootstrap;

namespace Eling.Backend.Tests;

public sealed class BootstrapValueTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("Yes", true)]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("on", false)]
    public void IsWatchEnabled_Matrix(string? raw, bool expected)
    {
        // Act
        var actual = FrontendReconcile.IsWatchEnabled(raw);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FindRepoRootWithPnpm_PointsAtDirWithBothMarkers_WhenFound()
    {
        // Act — walks up from the test binary dir, max 10 levels
        var root = DevModeDetector.FindRepoRootWithPnpm();

        // Assert — loose: either absent or a dir holding both markers
        if (root is not null)
        {
            Assert.True(File.Exists(Path.Combine(root, "package.json")));
            Assert.True(File.Exists(Path.Combine(root, "pnpm-lock.yaml")));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("")]
    public void IsDevMode_False_WhenWatchDisabled(string? raw)
    {
        // Arrange
        var previous = Environment.GetEnvironmentVariable("ELING_WATCH_DASHBOARD");
        Environment.SetEnvironmentVariable("ELING_WATCH_DASHBOARD", raw);
        try
        {
            // Act
            var actual = DevModeDetector.IsDevMode();

            // Assert — watch gate off short-circuits regardless of repo root
            Assert.False(actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_WATCH_DASHBOARD", previous);
        }
    }
}
