using Eling.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Desktop.Tests;

public sealed class DesktopSettingsStoreTests : IDisposable
{
    private readonly string _scopeDir = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}");
    private readonly string? _previousScope = Environment.GetEnvironmentVariable("ELING_USER_SCOPE");

    public DesktopSettingsStoreTests()
    {
        Directory.CreateDirectory(_scopeDir);
        Environment.SetEnvironmentVariable("ELING_USER_SCOPE", _scopeDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ELING_USER_SCOPE", _previousScope);
        if (Directory.Exists(_scopeDir))
            Directory.Delete(_scopeDir, recursive: true);
    }

    private static DesktopSettingsStore Store()
        => new(NullLogger<DesktopSettingsStore>.Instance);

    [Fact]
    public void BackendUrl_MissingFile_ReturnsNull()
    {
        // Act
        var actual = Store().GetBackendUrl();

        // Assert
        Assert.Null(actual);
    }

    [Fact]
    public void BackendUrl_RoundTrips()
    {
        // Act
        Store().SetBackendUrl("http://127.0.0.1:4417");
        var actual = Store().GetBackendUrl();

        // Assert
        Assert.Equal("http://127.0.0.1:4417", actual);
    }

    [Fact]
    public void WindowBounds_MissingFile_ReturnsNull()
    {
        // Act
        var actual = Store().GetWindowBounds();

        // Assert
        Assert.Null(actual);
    }

    [Fact]
    public void WindowBounds_RoundTrips_WhenValid()
    {
        // Arrange
        var bounds = new DesktopSettingsStore.WindowBounds(10, 20, 800, 600, false);

        // Act
        Store().SaveWindowBounds(bounds);
        var actual = Store().GetWindowBounds();

        // Assert
        Assert.Equal(bounds, actual);
    }

    [Theory]
    [InlineData(10, 20, 399, 600)]   // width below minimum
    [InlineData(10, 20, 800, 299)]   // height below minimum
    [InlineData(double.NaN, 20, 800, 600)]
    public void WindowBounds_Invalid_IsRejected(double x, double y, double w, double h)
    {
        // Arrange
        var bounds = new DesktopSettingsStore.WindowBounds(x, y, w, h, false);

        // Act
        Store().SaveWindowBounds(bounds);
        var actual = Store().GetWindowBounds();

        // Assert
        Assert.Null(actual);
    }

    [Fact]
    public void CorruptFile_ReturnsNull_WithoutThrowing()
    {
        // Arrange — write garbage where the settings file will be
        var dir = Path.Combine(_scopeDir, "config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "desktop-settings.json"), "{oops");

        // Act
        var url = Record.Exception(() => Store().GetBackendUrl());
        var bounds = Record.Exception(() => Store().GetWindowBounds());

        // Assert
        Assert.Null(url);
        Assert.Null(bounds);
    }
}
