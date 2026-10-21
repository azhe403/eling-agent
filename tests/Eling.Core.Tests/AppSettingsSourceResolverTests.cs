using Eling.Core.Database;

namespace Eling.Core.Tests;

public sealed class AppSettingsSourceResolverTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _contentRoot;

    public AppSettingsSourceResolverTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"eling-workspace-{Guid.NewGuid():N}");
        _contentRoot = Path.Combine(Path.GetTempPath(), $"eling-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_contentRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail the test run.
        }
    }

    [Fact]
    public void Resolve_ConfiguredSource_Wins()
    {
        // Arrange - an empty workspace, so only the override can answer.
        var configured = Path.Combine(_workspace, "custom", "appsettings.json");

        // Act
        var actual = AppSettingsSourceResolver.Resolve(
            contentRootPath: _contentRoot,
            workspaceDirectory: _workspace,
            configuredSource: configured);

        // Assert
        Assert.Equal(configured, actual);
    }

    [Fact]
    public void Resolve_WorkspaceHoldingAppSettings_Wins()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_workspace, "appsettings.json"), "{}");

        // Act - an empty configured source means "no override".
        var actual = AppSettingsSourceResolver.Resolve(
            contentRootPath: _contentRoot,
            workspaceDirectory: _workspace,
            configuredSource: "");

        // Assert
        Assert.Equal(_workspace, actual);
    }

    [Fact]
    public void Resolve_WorkspaceHoldingOnlyAnEnvironmentFile_StillWins()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_workspace, "appsettings.Staging.json"), "{}");

        // Act
        var actual = AppSettingsSourceResolver.Resolve(
            contentRootPath: _contentRoot,
            workspaceDirectory: _workspace,
            configuredSource: "");

        // Assert
        Assert.Equal(_workspace, actual);
    }

    [Fact]
    public void Resolve_WorkspaceWithoutAppSettings_FallsBackToTheContentRoot()
    {
        // Act
        var actual = AppSettingsSourceResolver.Resolve(
            contentRootPath: _contentRoot,
            workspaceDirectory: _workspace,
            configuredSource: "");

        // Assert
        Assert.Equal(_contentRoot, actual);
    }

    [Fact]
    public void Resolve_MissingWorkspace_FallsBackToTheContentRoot()
    {
        // Act
        var actual = AppSettingsSourceResolver.Resolve(
            contentRootPath: _contentRoot,
            workspaceDirectory: Path.Combine(_workspace, "does-not-exist"),
            configuredSource: "");

        // Assert
        Assert.Equal(_contentRoot, actual);
    }

    [Fact]
    public void HasAppSettings_IgnoresFilesThatAreNotAppSettings()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_workspace, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_workspace, "appsettings.Staging.json"), "{}");

        // Act
        var actual = AppSettingsSourceResolver.HasAppSettings(_workspace);

        // Assert
        Assert.True(actual);
    }

    [Fact]
    public void HasAppSettings_OnAMissingDirectory_IsFalse()
    {
        // Act
        var actual = AppSettingsSourceResolver.HasAppSettings(
            Path.Combine(_workspace, "does-not-exist"));

        // Assert
        Assert.False(actual);
    }
}
