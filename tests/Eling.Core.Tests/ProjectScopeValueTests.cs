using Eling.Core.Scope;

namespace Eling.Core.Tests;

public sealed class ProjectScopeValueTests
{
    [Fact]
    public void Decision_HasExpectedValues()
    {
        // Arrange & Act
        var values = Enum.GetValues<ProjectScopeDecision>();

        // Assert
        Assert.Equal(2, values.Length);
        Assert.True(Enum.IsDefined(typeof(ProjectScopeDecision), ProjectScopeDecision.Ask));
        Assert.True(Enum.IsDefined(typeof(ProjectScopeDecision), ProjectScopeDecision.Disabled));
    }

    [Fact]
    public void Entry_Equality_ByValue()
    {
        // Arrange
        var created = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

        // Act
        var left = new ProjectScopeEntry(ProjectScopeDecision.Ask, created, created);
        var right = new ProjectScopeEntry(ProjectScopeDecision.Ask, created, created);

        // Assert
        Assert.Equal(left, right);
    }

    [Fact]
    public void Entry_Inequality_WhenDecisionDiffers()
    {
        // Arrange
        var created = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

        // Act
        var ask = new ProjectScopeEntry(ProjectScopeDecision.Ask, created, created);
        var disabled = new ProjectScopeEntry(ProjectScopeDecision.Disabled, created, created);

        // Assert
        Assert.NotEqual(ask, disabled);
    }

    [Fact]
    public void Entry_AllowsNullDates()
    {
        // Act
        var entry = new ProjectScopeEntry(ProjectScopeDecision.Ask, null, null);

        // Assert
        Assert.Equal(ProjectScopeDecision.Ask, entry.Decision);
        Assert.Null(entry.CreatedAt);
        Assert.Null(entry.UpdatedAt);
    }

    [Fact]
    public void Entry_WithExpression_PreservesImmutability()
    {
        // Arrange
        var original = new ProjectScopeEntry(ProjectScopeDecision.Ask, null, null);

        // Act
        var updated = original with { Decision = ProjectScopeDecision.Disabled };

        // Assert
        Assert.Equal(ProjectScopeDecision.Ask, original.Decision);
        Assert.Equal(ProjectScopeDecision.Disabled, updated.Decision);
    }

    [Fact]
    public void Pattern_Equality_ByValue()
    {
        // Arrange
        var entry = new ProjectScopeEntry(ProjectScopeDecision.Ask, null, null);

        // Act
        var left = new ProjectScopePattern("/tmp/acme/**", entry);
        var right = new ProjectScopePattern("/tmp/acme/**", entry);

        // Assert
        Assert.Equal(left, right);
    }

    [Fact]
    public void Pattern_PreservesGlob()
    {
        // Arrange
        var entry = new ProjectScopeEntry(ProjectScopeDecision.Disabled, null, null);

        // Act
        var pattern = new ProjectScopePattern("/tmp/acme/**", entry);

        // Assert
        Assert.Equal("/tmp/acme/**", pattern.Glob);
        Assert.Equal(entry, pattern.Entry);
    }

    [Fact]
    public void Resolution_Equality_ByValue()
    {
        // Arrange
        var entry = new ProjectScopeEntry(ProjectScopeDecision.Disabled, null, null);

        // Act
        var left = new ProjectScopeResolution(
            ProjectScopeDecision.Disabled, ProjectScopeResolution.Level.Project, entry);
        var right = new ProjectScopeResolution(
            ProjectScopeDecision.Disabled, ProjectScopeResolution.Level.Project, entry);

        // Assert
        Assert.Equal(left, right);
    }

    [Fact]
    public void Resolution_Level_HasAllThreeValues()
    {
        // Arrange & Act
        var levels = Enum.GetValues<ProjectScopeResolution.Level>();

        // Assert
        Assert.Equal(3, levels.Length);
        Assert.Contains(ProjectScopeResolution.Level.Default, levels);
        Assert.Contains(ProjectScopeResolution.Level.Pattern, levels);
        Assert.Contains(ProjectScopeResolution.Level.Project, levels);
    }

    [Fact]
    public void Resolution_AllowsNullEntry_ForDefaultLevel()
    {
        // Act
        var resolution = new ProjectScopeResolution(
            ProjectScopeDecision.Ask, ProjectScopeResolution.Level.Default, null);

        // Assert
        Assert.Equal(ProjectScopeDecision.Ask, resolution.Decision);
        Assert.Equal(ProjectScopeResolution.Level.Default, resolution.MatchedLevel);
        Assert.Null(resolution.Entry);
    }
}
