using Eling.Core.Memory;

namespace Eling.Core.Tests;

public sealed class MemoryScopeValueTests
{
    [Fact]
    public void ScopeKind_HasThreeValues()
    {
        // Arrange & Act
        var values = Enum.GetValues<MemoryScopeKind>();

        // Assert
        Assert.Equal(3, values.Length);
        Assert.Contains(MemoryScopeKind.Project, values);
        Assert.Contains(MemoryScopeKind.ProjectLocal, values);
        Assert.Contains(MemoryScopeKind.Global, values);
    }

    [Theory]
    [InlineData("project", MemoryScopeKind.Project)]
    [InlineData("project-local", MemoryScopeKind.ProjectLocal)]
    [InlineData("global", MemoryScopeKind.Global)]
    [InlineData("auto", MemoryScopeKind.ProjectLocal)]
    public void TryParseScope_AcceptsAllValid(string input, MemoryScopeKind expected)
    {
        // Act
        var ok = MemoryScopeParser.TryParseScope(input, out var kind);

        // Assert
        Assert.True(ok);
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("  PROJECT  ", MemoryScopeKind.Project)]
    [InlineData("Project-Local", MemoryScopeKind.ProjectLocal)]
    [InlineData("GLOBAL", MemoryScopeKind.Global)]
    [InlineData(" Auto ", MemoryScopeKind.ProjectLocal)]
    public void TryParseScope_IsCaseInsensitive_AndTrims(string input, MemoryScopeKind expected)
    {
        // Act
        var ok = MemoryScopeParser.TryParseScope(input, out var kind);

        // Assert
        Assert.True(ok);
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("merged")]
    [InlineData("foo")]
    public void TryParseScope_RejectsNullEmptyUnknown(string? input)
    {
        // Act
        var ok = MemoryScopeParser.TryParseScope(input, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    public void TryParseSearchScope_DefaultsNullToMerged()
    {
        // Act
        var ok = MemoryScopeParser.TryParseSearchScope(null, out var normalized);

        // Assert
        Assert.True(ok);
        Assert.Equal("merged", normalized);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("project-local")]
    [InlineData("global")]
    [InlineData("merged")]
    [InlineData("  GLOBAL  ")]
    public void TryParseSearchScope_AcceptsAllFour(string input)
    {
        // Act
        var ok = MemoryScopeParser.TryParseSearchScope(input, out var normalized);

        // Assert
        Assert.True(ok);
        Assert.Equal(input.Trim().ToLowerInvariant(), normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("foo")]
    public void TryParseSearchScope_RejectsUnknown(string input)
    {
        // Act
        var ok = MemoryScopeParser.TryParseSearchScope(input, out _);

        // Assert
        Assert.False(ok);
    }

    [Theory]
    [InlineData(MemoryScopeKind.Project, "project")]
    [InlineData(MemoryScopeKind.ProjectLocal, "project-local")]
    [InlineData(MemoryScopeKind.Global, "global")]
    public void ToWireString_MapsAllKinds(MemoryScopeKind kind, string expected)
    {
        // Act
        var actual = MemoryScopeLabels.ToWireString(kind);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ToWireString_UnknownFallsBackToProject()
    {
        // Act
        var actual = MemoryScopeLabels.ToWireString((MemoryScopeKind)999);

        // Assert
        Assert.Equal("project", actual);
    }
}
