using System.Globalization;
using Avalonia.Media;
using Eling.Desktop.Converters;

namespace Eling.Desktop.Tests;

public sealed class ConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static Color BrushColor(object value)
        => Assert.IsType<SolidColorBrush>(value).Color;

    [Theory]
    [InlineData("connected", "Green")]
    [InlineData("CONNECTED", "Green")]
    [InlineData("connecting", "Orange")]
    [InlineData("disconnected", "Red")]
    [InlineData(null, "Red")]
    public void SseStatus_Matrix(string? status, string expectedName)
    {
        // Act
        var actual = SseStatusColorConverter.Instance.Convert(status, typeof(object), null, Culture);

        // Assert
        var expected = Color.Parse(expectedName);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SseStatus_ConvertBack_Throws()
    {
        // Act & Assert
        Assert.Throws<NotSupportedException>(
            () => SseStatusColorConverter.Instance.ConvertBack(null, typeof(object), null, Culture));
    }

    [Theory]
    [InlineData("project", "project", "#3b82f6")]
    [InlineData("Project", "project", "#3b82f6")]
    [InlineData("global", "project", "#262626")]
    [InlineData(null, "project", "#262626")]
    public void ScopeButton_Matrix(string? scope, string tag, string expectedHex)
    {
        // Act
        var actual = ScopeButtonConverter.Instance.Convert(scope, typeof(object), tag, Culture);

        // Assert
        Assert.Equal(Color.Parse(expectedHex), BrushColor(actual));
    }

    [Fact]
    public void ScopeButton_ConvertBack_Throws()
    {
        // Act & Assert
        Assert.Throws<NotSupportedException>(
            () => ScopeButtonConverter.Instance.ConvertBack(null, typeof(object), null, Culture));
    }

    [Fact]
    public void ProjectName_ExtractsFolderName()
    {
        // Arrange
        var path = Path.Combine("some", "dir", "myapp");

        // Act
        var actual = ProjectNameConverter.Instance.Convert(path, typeof(object), null, Culture);

        // Assert
        Assert.Equal("📁 myapp", actual);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void ProjectName_Passthrough_OnEmpty(string? path, string expected)
    {
        // Act
        var actual = ProjectNameConverter.Instance.Convert(path, typeof(object), null, Culture);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ProjectName_ConvertBack_Throws()
    {
        // Act & Assert
        Assert.Throws<NotSupportedException>(
            () => ProjectNameConverter.Instance.ConvertBack(null, typeof(object), null, Culture));
    }

    [Theory]
    [InlineData("fact", "#1e3a5f")]
    [InlineData("preference", "#3d1a45")]
    [InlineData("decision", "#472807")]
    [InlineData("lesson", "#143d22")]
    [InlineData("note", "#2e2e2e")]
    [InlineData("FACT", "#1e3a5f")]
    [InlineData("unknown", "#252525")]
    [InlineData(null, "#252525")]
    public void MemoryType_Matrix(string? type, string expectedHex)
    {
        // Act
        var actual = MemoryTypeColorConverter.Instance.Convert(type, typeof(object), null, Culture);

        // Assert
        Assert.Equal(Color.Parse(expectedHex), BrushColor(actual));
    }

    [Fact]
    public void MemoryType_ConvertBack_Throws()
    {
        // Act & Assert
        Assert.Throws<NotSupportedException>(
            () => MemoryTypeColorConverter.Instance.ConvertBack(null, typeof(object), null, Culture));
    }
}
